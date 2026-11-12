using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.VectorData;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class CollectionCatalogTests
{
    [Fact]
    public async Task ExistenceWithoutCatalog_IsNeverInferredFromSearchRouting()
    {
        using var harness = new TestHarness();
        await Assert.ThrowsAsync<NotSupportedException>(() => harness.Store.CollectionExistsAsync("article"));
        await Assert.ThrowsAsync<NotSupportedException>(() => harness.Collection.CollectionExistsAsync());
        Assert.Empty(harness.Handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task MissingSource_DoesNotPassSearchabilityValidation(HttpStatusCode status)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""
            {"root":{"errors":[{"code":4,"message":"Invalid query parameter","details":"Could not resolve source ref 'article'. Valid source refs are features."}]}}
            """, status));
        var error = await Assert.ThrowsAsync<VectorStoreException>(() => harness.Collection.EnsureCollectionExistsAsync());
        Assert.Contains("Could not resolve source ref", error.Message);
        Assert.Equal("EnsureCollectionExistsAsync", error.OperationName);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task OperationalErrors_AreNotReportedAsSuccessfulValidation(HttpStatusCode status)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""
            {"root":{"errors":[{"code":4,"message":"Invalid query parameter","details":"Could not resolve source ref 'article'."}]}}
            """, status));
        var error = await Assert.ThrowsAsync<VectorStoreException>(() => harness.Collection.EnsureCollectionExistsAsync());
        Assert.Equal("EnsureCollectionExistsAsync", error.OperationName);
        Assert.Equal("article", error.CollectionName);
        Assert.NotNull(error.InnerException);
    }

    [Theory]
    [InlineData("{\"root\":{\"coverage\":{\"full\":false}}}")]
    [InlineData("{\"root\":{\"errors\":[{\"code\":4,\"message\":\"Invalid rank profile\"}]}}")]
    [InlineData("{\"root\":{\"errors\":[{\"code\":4,\"message\":\"Could not resolve source ref 'other'.\"}]}}")]
    public async Task IncompleteOrUnrelatedFailures_RemainErrors(string body)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(body));
        await Assert.ThrowsAsync<VectorStoreException>(() => harness.Collection.EnsureCollectionExistsAsync());
    }

    [Fact]
    public async Task ConfiguredCatalog_IsAuthoritativeAndIncludesEmptySchemas()
    {
        using var harness = new TestHarness();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { CollectionCatalog = new Catalog() });
        Assert.True(await store.CollectionExistsAsync("empty"));
        Assert.False(await store.GetCollection<string, Article>("absent").CollectionExistsAsync());
        Assert.Equal(new[] { "article", "empty" }, await store.ListCollectionNamesAsync().Collect());
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task EnsureRequiresAnExplicitDeploymentAssertionOrCatalog()
    {
        using var harness = new TestHarness();
        using var store = new VespaVectorStore(harness.Client, new() { Namespace = "test" });
        var collection = (VespaVectorStoreCollection<string, Article>)store.GetCollection<string, Article>("article");
        await Assert.ThrowsAsync<NotSupportedException>(() => collection.EnsureCollectionExistsAsync());
        Assert.Empty(harness.Handler.Requests);
        await collection.ValidateSearchabilityAsync();
        Assert.Equal("/search/", Assert.Single(harness.Handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task EnsureRechecksCatalogAndNeverTreatsAnAssertionAsAnOverride()
    {
        using var harness = new TestHarness();
        var catalog = new Catalog();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { CollectionCatalog = catalog });
        using var collection = store.GetCollection<string, Article>("article");
        await collection.EnsureCollectionExistsAsync();
        Assert.Single(harness.Handler.Requests);
        catalog.Present = false;
        var error = await Assert.ThrowsAsync<VectorStoreException>(() => collection.EnsureCollectionExistsAsync());
        Assert.Contains("Deploy", error.Message);
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task CrudDoesNotRequireARankProfileButSearchDoes()
    {
        using var harness = new TestHarness();
        using var store = new VespaVectorStore(harness.Client, new() { Namespace = "test" });
        using var collection = store.GetDynamicCollection("article", new()
        {
            Properties = [new VectorStoreKeyProperty("Id", typeof(string)),
                new VectorStoreVectorProperty("Vector", typeof(ReadOnlyMemory<float>), 3)]
        });
        await collection.UpsertAsync(new Dictionary<string, object?> { ["Id"] = "a", ["Vector"] = new float[] { 1, 0, 0 } });
        Assert.Single(harness.Handler.Requests);
        await Assert.ThrowsAsync<NotSupportedException>(() => collection.SearchAsync(new float[] { 1, 0, 0 }, 1).Collect());
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task FlatVectorMetadataForcesExactSearch()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit()));
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { Approximate = true, HnswExploreAdditionalHits = 10 });
        using var collection = store.GetDynamicCollection("article", new()
        {
            Properties = [new VectorStoreKeyProperty("Id", typeof(string)),
                new VectorStoreVectorProperty("Vector", typeof(ReadOnlyMemory<float>), 3) { StorageName = "embedding", IndexKind = IndexKind.Flat }]
        });
        Assert.Single(await collection.SearchAsync(new float[] { 1, 0, 0 }, 1).Collect());
        var body = Assert.Single(harness.Handler.Requests).Body;
        Assert.Contains("approximate:false", body);
        Assert.DoesNotContain("exploreAdditionalHits", body);
    }

    [Fact]
    public async Task CanceledProbeAndEnumeration_DoNoWork()
    {
        using var harness = new TestHarness();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { CollectionCatalog = new Catalog() });
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CollectionExistsAsync("article", canceled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => store.ListCollectionNamesAsync(canceled.Token));
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public void MutableVectorBindings_AreSnapshottedAndValidated()
    {
        using var harness = new TestHarness();
        Dictionary<string, VespaVectorFieldOptions> bindings = new()
        {
            [nameof(Article.Embedding)] = new() { RankProfile = "vector_search", QueryTensorName = "query_embedding" }
        };
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { VectorFields = bindings });
        bindings[nameof(Article.Embedding)] = new() { RankProfile = "bad identifier" };
        using var collection = store.GetCollection<string, Article>("article");
        Assert.Throws<ArgumentException>(() => new VespaVectorStore(harness.Client,
            TestHarness.Options with { VectorFields = bindings }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VespaVectorStore(harness.Client,
            TestHarness.Options with { HybridVectorTargetHits = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VespaVectorStore(harness.Client,
            TestHarness.Options with { HybridKeywordTargetHits = -1 }));
    }

    private sealed class Catalog : IVespaCollectionCatalog
    {
        internal bool Present { get; set; } = true;
        public Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Present && name is "article" or "empty");
        }
        public async IAsyncEnumerable<string> ListCollectionNamesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var name in new[] { "article", "empty", "article" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return name;
            }
        }
    }
}
