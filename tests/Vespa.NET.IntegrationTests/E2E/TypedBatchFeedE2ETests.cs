using System.Collections.Concurrent;
using Vespa.Documents;
using Vespa.Feed;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Models.Attributes;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public sealed class TypedBatchFeedE2ETests(VespaFixture fixture)
{
    [Theory]
    [InlineData("bulk-put")]
    [InlineData("stream-put")]
    [InlineData("bulk-update")]
    public async Task Batch_RejectsWrongTargetsLocallyAndAppliesFrozenConditionsInItsNamespace(string operation)
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var prefix = $"typed-batch-{Guid.NewGuid():N}";
        var batchNamespace = $"batch-{Guid.NewGuid():N}";
        var wrongTargetId = prefix + "-wrong-target";
        var rejectedId = prefix + "-rejected";
        var acceptedId = prefix + "-accepted";
        var documents = fixture.Client.Documents;
        var options = new VespaClientOptions
        {
            Endpoint = fixture.Endpoint ?? Environment.GetEnvironmentVariable("VESPA_ENDPOINT")!,
            DefaultNamespace = "test",
            EnableRetry = false
        };
        var recorder = new RecordingHandler(VespaServiceCollectionExtensions.BuildSocketsHttpHandler(options));
        using var http = new HttpClient(recorder);
        await using var client = new VespaClient(http, options);

        try
        {
            await Seed(wrongTargetId, "Protected");
            await Seed(rejectedId, "Locked");
            await Seed(acceptedId, "Ready");
            await documents.PutAsync(acceptedId, new TestProduct { Name = "Ready" });

            var expected = "Ready";
            FeedResult result;
            if (operation == "bulk-update")
            {
                BulkFieldUpdate[] rows =
                [
                    BulkFieldUpdate.Create<ProductFields>(wrongTargetId, Rename("Wrong target"),
                        documentType: "event", condition: p => true),
                    BulkFieldUpdate.Create<ProductFields>(rejectedId, Rename("Ready"),
                        documentType: "product", condition: p => p.Label == expected),
                    BulkFieldUpdate.Create<TestProduct>(acceptedId, update =>
                    {
                        expected = "Changed in configure";
                        update.Field(p => p.Name, FieldOp.Assign("Applied"));
                    }, condition: p => p.Name == expected)
                ];
                expected = "Changed after construction";
                result = await client.Feed.BulkUpdateAsync(rows, "product", @namespace: batchNamespace, maxConcurrency: 1);
            }
            else
            {
                FeedDocument<ProductFields>[] rows =
                [
                    FeedDocument.Create<ProductFields>(wrongTargetId, new() { Label = "Wrong target" },
                        documentType: "event", condition: p => true),
                    FeedDocument.Create<ProductFields>(rejectedId, new() { Label = "Ready" },
                        documentType: "product", condition: p => p.Label == expected),
                    FeedDocument.Create<ProductFields>(acceptedId, new() { Label = "Applied" },
                        documentType: "product", condition: p => p.Label == expected)
                ];
                expected = "Changed after construction";
                result = operation == "stream-put"
                    ? await client.Feed.FeedAsync(Stream(), "product", @namespace: batchNamespace, maxConcurrency: 1, boundedCapacity: 1)
                    : await client.Feed.BulkPutAsync(rows, "product", @namespace: batchNamespace, maxConcurrency: 1);

                async IAsyncEnumerable<FeedDocument<ProductFields>> Stream()
                {
                    await Task.Yield();
                    expected = "Changed on enumeration";
                    foreach (var row in rows)
                        yield return row;
                }
            }

            Assert.Equal(3, result.TotalOperations);
            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(2, result.FailureCount);
            Assert.Null(Assert.Single(result.Errors, error => error.DocumentId == wrongTargetId).StatusCode);
            Assert.Equal(412, Assert.Single(result.Errors, error => error.DocumentId == rejectedId).StatusCode);
            Assert.Equal(new[] { rejectedId, acceptedId }, recorder.DocumentIds.ToArray());
            Assert.Equal("Protected", await Read(wrongTargetId));
            Assert.Equal("Locked", await Read(rejectedId));
            Assert.Equal("Applied", await Read(acceptedId));
            Assert.Equal("Ready", (await documents.GetAsync<TestProduct>(acceptedId))!.Fields!.Name);
        }
        finally
        {
            foreach (var id in new[] { wrongTargetId, rejectedId, acceptedId })
                await documents.DeleteAsync(id, "product", @namespace: batchNamespace);
            await documents.DeleteAsync<TestProduct>(acceptedId);
        }

        Task<VespaResponse> Seed(string id, string name) =>
            documents.PutAsync(id, new TestProduct { Name = name }, documentType: "product", @namespace: batchNamespace);

        async Task<string?> Read(string id) =>
            (await documents.GetAsync<TestProduct>(id, "product", @namespace: batchNamespace))?.Fields?.Name;
    }

    private static Dictionary<string, FieldOperation> Rename(string name) => new() { ["product_name"] = FieldOp.Assign(name) };

    private sealed class ProductFields
    {
        [VespaField(Name = "product_name")]
        public string Label { get; init; } = "";
    }

    private sealed class RecordingHandler(HttpMessageHandler transport) : DelegatingHandler(transport)
    {
        internal ConcurrentQueue<string> DocumentIds { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            DocumentIds.Enqueue(request.RequestUri!.Segments[^1]);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
