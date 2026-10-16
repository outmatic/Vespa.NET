using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public sealed class DocumentApiImprovementsE2ETests(VespaFixture fixture)
{
    private static readonly DocumentRequestOptions WriteOptions = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly DocumentRequestOptions ReadOptions = new() { Cluster = "content", Timeout = TimeSpan.FromSeconds(20) };

    [Fact]
    public async Task TypedCrud_WithOptions_ResolvesSchemaAndNamespace()
    {
        if (!VespaFixture.IntegrationEnabled) return;

        var id = $"typed-options-{Guid.NewGuid():N}";
        var documents = fixture.Client.Documents;
        try
        {
            await documents.PutAsync(WriteOptions, id, new TestProduct { Name = "Typed options", Price = 10 });
            var before = await documents.GetAsync<TestProduct>(ReadOptions, id, fieldSet: "product:product_name,price");
            Assert.Equal("Typed options", before!.Fields!.Name);
            Assert.Equal(10, before.Fields.Price);

            await documents.UpdateFieldsAsync<TestProduct>(WriteOptions, id,
                update => update.Field(product => product.Price, FieldOp.Assign(25.0)));

            var after = Assert.Single(await documents.GetManyAsync<TestProduct>(ReadOptions, [id]));
            Assert.Equal(25, after.Fields!.Price);
            Assert.Equal($"id:test:product::{id}", after.FullId);

            await documents.DeleteAsync<TestProduct>(WriteOptions, id);
            Assert.Null(await documents.GetAsync<TestProduct>(ReadOptions, id));
        }
        finally
        {
            await documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TypedAddressedCrud_WithOptions_RoundTrips(bool groupAddressing)
    {
        if (!VespaFixture.IntegrationEnabled) return;

        var group = $"typed-options-{Guid.NewGuid():N}";
        var localId = $"{group}:part";
        const long number = 42;
        var documents = fixture.Client.Documents;
        try
        {
            var item = new TestEvent { EventName = "Typed options", Payload = "before" };
            if (groupAddressing)
            {
                await documents.PutByGroupAsync(group, localId, item, requestOptions: WriteOptions);
                await documents.UpdateFieldsByGroupAsync<TestEvent>(WriteOptions, group, localId,
                    update => update.Field(document => document.Payload, FieldOp.Assign("after")));
            }
            else
            {
                await documents.PutByNumberAsync(number, localId, item, requestOptions: WriteOptions);
                await documents.UpdateFieldsByNumberAsync<TestEvent>(WriteOptions, number, localId,
                    update => update.Field(document => document.Payload, FieldOp.Assign("after")));
            }

            var read = groupAddressing
                ? await documents.GetByGroupAsync<TestEvent>(ReadOptions, group, localId)
                : await documents.GetByNumberAsync<TestEvent>(ReadOptions, number, localId);
            Assert.Equal("after", read!.Fields!.Payload);
            Assert.Equal(localId, read.Id);
        }
        finally
        {
            if (groupAddressing)
                await documents.DeleteByGroupAsync<TestEvent>(group, localId, requestOptions: WriteOptions);
            else
                await documents.DeleteByNumberAsync<TestEvent>(number, localId, requestOptions: WriteOptions);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VisitPages_CanResumeAcrossNewClientInstances(bool root)
    {
        if (!VespaFixture.IntegrationEnabled) return;

        var category = $"visit-pages-{Guid.NewGuid():N}";
        var ids = Enumerable.Range(0, 16).Select(i => $"{category}-{i}").ToArray();
        try
        {
            await Task.WhenAll(ids.Select(id => fixture.Client.Documents.PutAsync(WriteOptions, id,
                new TestProduct { Name = id, Category = category })));

            var endpoint = fixture.Endpoint ?? Environment.GetEnvironmentVariable("VESPA_ENDPOINT")!;
            var options = new VisitOptions { WantedDocumentCount = 1, Concurrency = 1 };
            var selection = $"product.category == \"{category}\"";
            var visited = new List<VespaDocument<TestProduct>>();
            string? continuation = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            do
            {
                // Recreate the client between pages: the continuation is the only
                // progress state carried over, as when resuming in another process.
                await using var reader = new VespaClient(new VespaClientOptions
                {
                    Endpoint = endpoint,
                    DefaultNamespace = "fallback",
                    EnableRetry = false,
                    EnableCircuitBreaker = false
                });
                var page = root
                    ? await reader.Documents.VisitAllPageAsync<TestProduct>("content", selection, continuation, options, deadline.Token)
                    : await reader.Documents.VisitPageAsync<TestProduct>(selection: selection, cluster: "content",
                        continuation: continuation, options: options, cancellationToken: deadline.Token);
                visited.AddRange(page.Documents);
                continuation = page.Continuation;
            }
            while (continuation is not null);

            Assert.Equal(ids.Order(), visited.Select(document => document.Id).Order());
            Assert.All(visited, document => Assert.StartsWith("id:test:product::", document.FullId));
        }
        finally
        {
            await fixture.Client.Feed.BulkDeleteAsync(ids, "product", @namespace: "test");
        }
    }
}
