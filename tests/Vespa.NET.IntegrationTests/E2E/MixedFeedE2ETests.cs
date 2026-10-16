using Vespa.Documents;
using Vespa.Feed;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public sealed class MixedFeedE2ETests(VespaFixture fixture)
{
    [Fact]
    public async Task MixedFeed_SupportsSchemasNamespacesConditionsAndUpserts()
    {
        if (!VespaFixture.IntegrationEnabled) return;

        var prefix = $"mixed-{Guid.NewGuid():N}";
        var keep = $"{prefix}-keep";
        var doomed = $"{prefix}-doomed";
        var upsert = $"{prefix}-upsert";
        var removed = $"{prefix}-removed";
        var explicitId = $"{prefix}-explicit";
        var otherNamespace = $"{prefix}-namespace";
        var eventId = $"id:test:event:g={prefix}:event:one";
        var documents = fixture.Client.Documents;
        var requestOptions = new DocumentRequestOptions { Timeout = TimeSpan.FromSeconds(20), TraceLevel = 1 };
        try
        {
            await documents.PutAsync(doomed, new TestProduct { Name = "Delete me" });
            FeedOperation[] operations =
            [
                FeedOperation.Put(keep, new TestProduct { Name = "Before", Price = 1 },
                    condition: "product.price > 0", create: true, requestOptions: requestOptions),
                FeedOperation.Put(eventId, new TestEvent { EventName = "Mixed event", Payload = "before" }),
                FeedOperation.Put(keep, new TestProduct { Name = "Other namespace", Price = 8 }, @namespace: otherNamespace),
                FeedOperation.Update<TestProduct>($"id:test:product::{keep}", update => update
                    .Field(product => product.Name, FieldOp.Assign("After"))
                    .Field(product => product.Price, FieldOp.Assign(2.0)),
                    condition: "product.price == 1", requestOptions: requestOptions),
                FeedOperation.Delete<TestProduct>(doomed, requestOptions: requestOptions),
                FeedOperation.Update<TestEvent>(eventId, update => update.Field(item => item.Payload, FieldOp.Assign("after"))),
                FeedOperation.Update<TestProduct>(upsert, update => update
                    .Field(product => product.Name, FieldOp.Assign("Created by update"))
                    .Field(product => product.Price, FieldOp.Assign(3.0)), createIfMissing: true),
                FeedOperation.Put(removed, new TestProduct { Name = "Temporary" }),
                FeedOperation.Delete<TestProduct>(removed),
                FeedOperation.Update<TestProduct>(keep, update => update.Field(product => product.Price, FieldOp.Assign(9.0)), @namespace: otherNamespace),
                FeedOperation.Put(explicitId, new { product_name = "Explicit schema", price = 7.0 }, documentType: "product")
            ];

            var result = await fixture.Client.Feed.FeedAsync(operations, new() { MaxConcurrency = 8, BoundedCapacity = 3 });

            Assert.Equal(11, result.TotalOperations);
            Assert.Equal(11, result.SuccessCount);
            Assert.Empty(result.Errors);
            var kept = await documents.GetAsync<TestProduct>(keep);
            Assert.Equal("After", kept!.Fields!.Name);
            Assert.Equal(2, kept.Fields.Price);
            var other = await documents.GetAsync<TestProduct>(keep, "product", @namespace: otherNamespace);
            Assert.Equal("Other namespace", other!.Fields!.Name);
            Assert.Equal(9, other.Fields.Price);
            var created = await documents.GetAsync<TestProduct>(upsert);
            Assert.Equal("Created by update", created!.Fields!.Name);
            Assert.Equal(3, created.Fields.Price);
            var explicitDocument = await documents.GetAsync<TestProduct>(explicitId);
            Assert.Equal("Explicit schema", explicitDocument!.Fields!.Name);
            Assert.Equal(7, explicitDocument.Fields.Price);
            var item = await documents.GetAsync<TestEvent>(eventId, "event", @namespace: "test");
            Assert.Equal("after", item!.Fields!.Payload);
            Assert.Null(await documents.GetAsync<TestProduct>(doomed));
            Assert.Null(await documents.GetAsync<TestProduct>(removed));
        }
        finally
        {
            await fixture.Client.Feed.BulkDeleteAsync([keep, doomed, upsert, removed, explicitId], "product", @namespace: "test");
            await documents.DeleteAsync(keep, "product", @namespace: otherNamespace);
            await documents.DeleteAsync(eventId, "event", @namespace: "test");
        }
    }

    [Fact]
    public async Task MixedFeed_ConditionFailureIsReportedAndLaterUpdatesContinue()
    {
        if (!VespaFixture.IntegrationEnabled) return;

        var id = $"mixed-condition-{Guid.NewGuid():N}";
        try
        {
            FeedOperation[] operations =
            [
                FeedOperation.Put(id, new TestProduct { Name = "Before", Price = 1 }),
                FeedOperation.Update<TestProduct>(id, update => update.Field(product => product.Name, FieldOp.Assign("Rejected")), condition: "product.price == 999"),
                FeedOperation.Update<TestProduct>(id, update => update.Field(product => product.Name, FieldOp.Assign("Accepted")), condition: "product.price == 1"),
                FeedOperation.Delete<TestProduct>($"{id}-missing")
            ];

            var result = await fixture.Client.Feed.FeedAsync(operations, new() { BoundedCapacity = 2 });

            Assert.Equal(4, result.TotalOperations);
            Assert.Equal(3, result.SuccessCount);
            Assert.Equal(1, result.FailureCount);
            var error = Assert.Single(result.Errors);
            Assert.Equal(FeedOperationKind.Update, error.OperationKind);
            Assert.Equal("product", error.DocumentType);
            Assert.Equal("test", error.Namespace);
            Assert.Equal(1, error.OperationIndex);
            Assert.Equal(412, error.StatusCode);
            var document = await fixture.Client.Documents.GetAsync<TestProduct>(id);
            Assert.Equal("Accepted", document!.Fields!.Name);
            Assert.Equal(1, document.Fields.Price);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedFeed_AsyncSourcePreservesOrderAcrossDocumentIdAliases(bool numberAddressing)
    {
        if (!VespaFixture.IntegrationEnabled) return;

        var prefix = $"mixed-order-{Guid.NewGuid():N}";
        var firstLocation = numberAddressing ? "n=00042" : $"g={prefix}";
        var canonicalLocation = numberAddressing ? "n=42" : $"g={prefix}";
        var firstId = $"id:test:event:{firstLocation}:{prefix}:part";
        var canonicalId = $"id:test:event:{canonicalLocation}:{prefix}:part";
        FeedOperation[] operations =
        [
            FeedOperation.Put(firstId, new TestEvent { EventName = "Initial", Payload = "initial" }),
            FeedOperation.Update<TestEvent>(canonicalId, update => update.Field(item => item.Payload, FieldOp.Assign("updated"))),
            FeedOperation.Delete<TestEvent>(firstId),
            FeedOperation.Put(canonicalId, new TestEvent { EventName = "Replacement", Payload = "replacement" }),
            FeedOperation.Update<TestEvent>(firstId, update => update.Field(item => item.EventName, FieldOp.Assign("Final")))
        ];
        async IAsyncEnumerable<FeedOperation> Source()
        {
            foreach (var operation in operations)
            {
                await Task.Yield();
                yield return operation;
            }
        }
        try
        {
            var result = await fixture.Client.Feed.FeedAsync(Source(), new() { MaxConcurrency = 8, BoundedCapacity = 1 });

            Assert.Equal(5, result.SuccessCount);
            Assert.Empty(result.Errors);
            var document = await fixture.Client.Documents.GetAsync<TestEvent>(canonicalId, "event", @namespace: "test");
            Assert.Equal("Final", document!.Fields!.EventName);
            Assert.Equal("replacement", document.Fields.Payload);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync(canonicalId, "event", @namespace: "test");
        }
    }
}
