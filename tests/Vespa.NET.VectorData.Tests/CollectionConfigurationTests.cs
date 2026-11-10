using System.Text.Json;
using Microsoft.Extensions.VectorData;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class CollectionConfigurationTests
{
    [Fact]
    public async Task TypedCollections_UseIndependentOptionsWithoutChangingStoreDefaults()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""{"root":{"children":[]}}"""));
        var defaults = TestHarness.Options with
        {
            RankProfile = "article_rank",
            SummaryClass = "article_data",
            RequiredDataFields = ["category"]
        };
        using var store = new VespaVectorStore(harness.Client, defaults);
        using var articles = store.GetCollection<string, Article>("article");
        using var memories = store.GetCollection<Guid, GuidRecord>("memory", null, defaults with
        {
            RankProfile = "memory_rank",
            SummaryClass = "memory_data",
            RequiredDataFields = ["text"]
        });
        VectorStore microsoftStore = store;
        using var laterArticles = microsoftStore.GetCollection<string, Article>("article", null);

        Assert.Same(harness.Client, articles.GetService(typeof(IVespaClient)));
        Assert.Same(harness.Client, memories.GetService(typeof(IVespaClient)));
        await articles.SearchAsync(new float[] { 1, 0, 0 }, 1).Collect();
        await memories.SearchAsync(new float[] { 1, 0, 0 }, 1).Collect();
        await laterArticles.SearchAsync(new float[] { 1, 0, 0 }, 1).Collect();
        Assert.Equal(3, harness.Handler.Requests.Count);
        AssertQuery(harness.Handler.Requests[0].Body!, "article", "article_rank", "article_data");
        AssertQuery(harness.Handler.Requests[1].Body!, "memory", "memory_rank", "memory_data");
        AssertQuery(harness.Handler.Requests[2].Body!, "article", "article_rank", "article_data");

        await Assert.ThrowsAsync<ArgumentException>(() => articles.UpsertAsync(new Article
        {
            Id = "a", Category = null!, Embedding = new float[] { 1, 0, 0 }
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => memories.UpsertAsync(new GuidRecord
        {
            Id = Guid.NewGuid(), Text = null!, Embedding = new float[] { 1, 0, 0 }
        }));
        Assert.Equal(3, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task DynamicCollection_UsesItsOwnOptionsOnTheSharedClient()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""{"root":{"children":[]}}"""));
        var defaults = TestHarness.Options with { RequiredDataFields = ["category"] };
        using var store = new VespaVectorStore(harness.Client, defaults);
        using var collection = store.GetDynamicCollection("memory", DynamicCollectionTests.Definition(), defaults with
        {
            RankProfile = "memory_rank",
            SummaryClass = "memory_data",
            VectorSummaryClass = "memory_vectors",
            RequiredDataFields = ["content"]
        });

        Assert.Same(harness.Client, collection.GetService(typeof(IVespaClient)));
        await collection.SearchAsync(new float[] { 1, 0, 0 }, 1).Collect();
        await collection.SearchAsync(new float[] { 1, 0, 0 }, 1, new() { IncludeVectors = true }).Collect();
        AssertQuery(harness.Handler.Requests[0].Body!, "memory", "memory_rank", "memory_data");
        AssertQuery(harness.Handler.Requests[1].Body!, "memory", "memory_rank", "memory_vectors");
        await Assert.ThrowsAsync<ArgumentException>(() => collection.UpsertAsync(new Dictionary<string, object?>
        {
            ["Key"] = Guid.NewGuid(), ["Vector"] = new float[] { 1, 0, 0 }
        }));
        Assert.Equal(2, harness.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("namespace")]
    [InlineData("rank")]
    [InlineData("summary")]
    [InlineData("concurrency")]
    [InlineData("required")]
    [InlineData("null")]
    public void InvalidCollectionOptions_FailBeforeIO(string invalid)
    {
        using var harness = new TestHarness();
        var options = invalid switch
        {
            "namespace" => TestHarness.Options with { Namespace = "invalid:namespace" },
            "rank" => TestHarness.Options with { RankProfile = "invalid rank" },
            "summary" => TestHarness.Options with { SummaryClass = " " },
            "concurrency" => TestHarness.Options with { UpsertConcurrency = 0 },
            "required" => TestHarness.Options with { RequiredDataFields = ["unmapped"] },
            _ => null!
        };
        Assert.ThrowsAny<ArgumentException>(() => harness.Store.GetCollection<string, Article>("article", null, options));
        Assert.ThrowsAny<ArgumentException>(() => harness.Store.GetDynamicCollection("memory", DynamicCollectionTests.Definition(), options));
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task RequiredFields_AreSnapshotsForStoreDefaultsAndCollectionOverrides()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""{"fields":{}}"""));
        List<string> defaultFields = ["category"];
        List<string> overrideFields = ["text"];
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { RequiredDataFields = defaultFields });
        defaultFields.Clear();
        using var articles = store.GetCollection<string, Article>("article");
        using var memories = store.GetCollection<Guid, GuidRecord>("memory", null,
            TestHarness.Options with { RequiredDataFields = overrideFields });
        overrideFields.Clear();

        var articleError = await Assert.ThrowsAsync<VectorStoreException>(() => articles.GetAsync("a"));
        var memoryError = await Assert.ThrowsAsync<VectorStoreException>(() => memories.GetAsync(Guid.NewGuid()));
        Assert.Contains("category", articleError.Message);
        Assert.Contains("text", memoryError.Message);
        Assert.Equal(2, harness.Handler.Requests.Count);
    }

    [Fact]
    public void DisposedStore_RejectsCollectionOverrides()
    {
        using var harness = new TestHarness();
        harness.Store.Dispose();
        Assert.Throws<ObjectDisposedException>(() => harness.Store.GetCollection<string, Article>("article", null, TestHarness.Options));
        Assert.Throws<ObjectDisposedException>(() => harness.Store.GetDynamicCollection("memory", DynamicCollectionTests.Definition(), TestHarness.Options));
        Assert.Empty(harness.Handler.Requests);
    }

    private static void AssertQuery(string body, string schema, string rank, string summary)
    {
        using var json = JsonDocument.Parse(body);
        Assert.Equal(schema, json.RootElement.GetProperty("model.restrict").GetString());
        Assert.Equal(rank, json.RootElement.GetProperty("ranking").GetProperty("profile").GetString());
        Assert.Equal(summary, json.RootElement.GetProperty("presentation.summary").GetString());
    }
}
