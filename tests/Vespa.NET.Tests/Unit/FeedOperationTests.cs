using Vespa.Feed;
using Vespa.Models;
using Vespa.Models.Attributes;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class FeedOperationTests
{
    [Fact]
    public void Factories_InferMetadataAndRetainOptions()
    {
        var options = new DocumentRequestOptions { Route = "default/chain.indexing", Timeout = TimeSpan.FromSeconds(2) };
        FeedOperation[] operations =
        [
            FeedOperation.Put("item", new Item(), condition: "inventory.unit_price > 0", create: true, requestOptions: options),
            FeedOperation.Update<Item>("item", update => update.Field(item => item.Price, FieldOp.Assign(12)), requestOptions: options),
            FeedOperation.Update<Item>("item", new Dictionary<string, FieldOperation> { ["unit_price"] = FieldOp.Assign(12) }, requestOptions: options),
            FeedOperation.Delete<Item>("item", requestOptions: options)
        ];

        Assert.Equal([FeedOperationKind.Put, FeedOperationKind.Update, FeedOperationKind.Update, FeedOperationKind.Delete], operations.Select(operation => operation.Kind));
        Assert.All(operations, operation =>
        {
            Assert.Equal("item", operation.Id);
            Assert.Equal("inventory", operation.DocumentType);
            Assert.Equal("tenant", operation.Namespace);
            Assert.Same(options, operation.RequestOptions);
        });
        Assert.Equal("inventory.unit_price > 0", operations[0].Condition);
    }

    [Fact]
    public void ExplicitTargets_AllowModelsWithoutAttributesAndUseClientNamespace()
    {
        FeedOperation[] operations =
        [
            FeedOperation.Put("item", new { price = 12 }, documentType: "custom"),
            FeedOperation.Update("item", new Dictionary<string, FieldOperation>(), "custom"),
            FeedOperation.Delete("item", "custom")
        ];

        Assert.All(operations, operation =>
        {
            Assert.Equal("custom", operation.DocumentType);
            Assert.Null(operation.Namespace);
        });
        Assert.Equal("other", FeedOperation.Put("item", new Item(), @namespace: "other").Namespace);
        Assert.Null(FeedOperation.Put("item", new Item(), documentType: "custom").Namespace);
    }

    [Fact]
    public void InferredTarget_RequiresDocumentMetadata()
    {
        Assert.Throws<InvalidOperationException>(() => FeedOperation.Put("item", new object()));
        Assert.Throws<InvalidOperationException>(() => FeedOperation.Delete<object>("item"));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("schema")]
    [InlineData("namespace")]
    [InlineData("document")]
    [InlineData("configure")]
    [InlineData("fields")]
    public void Factories_RejectInvalidArguments(string argument)
    {
        Assert.ThrowsAny<ArgumentException>(() => argument switch
        {
            "id" => FeedOperation.Delete<Item>(""),
            "schema" => FeedOperation.Delete("item", " "),
            "namespace" => FeedOperation.Delete<Item>("item", @namespace: " "),
            "document" => FeedOperation.Put<Item>("item", null!),
            "configure" => FeedOperation.Update<Item>("item", (Action<Vespa.Documents.TypedFieldUpdateBuilder<Item>>)null!),
            _ => FeedOperation.Update("item", null!, "inventory")
        });
    }

    [VespaDocument("inventory", Namespace = "tenant")]
    private sealed class Item
    {
        [VespaField(Name = "unit_price")]
        public int Price { get; init; }
    }
}
