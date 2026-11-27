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

    [Theory]
    [InlineData(CatalogStage.List)]
    [InlineData(CatalogStage.GetEnumerator)]
    [InlineData(CatalogStage.Current)]
    [InlineData(CatalogStage.MoveNext)]
    [InlineData(CatalogStage.Dispose)]
    public async Task Listing_ReportsOperationalErrorsAtTheEnumerationStepThatFailed(CatalogStage stage)
    {
        using var harness = new TestHarness();
        var cause = new HttpRequestException("Catalog is unavailable.");
        var catalog = new FaultingCatalog(stage, cause);
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { CollectionCatalog = catalog });
        var names = store.ListCollectionNamesAsync().GetAsyncEnumerator();
        Assert.Equal(0, catalog.ListCalls);

        await using (names)
        {
            if (stage is CatalogStage.MoveNext or CatalogStage.Dispose)
            {
                Assert.True(await names.MoveNextAsync());
                Assert.Equal("article", names.Current);
                Assert.Equal(1, catalog.MoveCalls);
            }

            var error = stage is CatalogStage.Dispose
                ? await Assert.ThrowsAsync<VectorStoreException>(() => names.DisposeAsync().AsTask())
                : await Assert.ThrowsAsync<VectorStoreException>(() => names.MoveNextAsync().AsTask());
            Assert.Equal("vespa", error.VectorStoreSystemName);
            Assert.Equal("ListCollectionNamesAsync", error.OperationName);
            Assert.Null(error.CollectionName);
            Assert.Same(cause, error.InnerException);
        }
        Assert.Equal(stage is CatalogStage.List or CatalogStage.GetEnumerator ? 0 : 1, catalog.DisposeCalls);
        Assert.Empty(harness.Handler.Requests);
    }

    [Theory]
    [InlineData("store", "CollectionExistsAsync")]
    [InlineData("collection", "CollectionExistsAsync")]
    [InlineData("ensure", "EnsureCollectionExistsAsync")]
    public async Task ExistenceAndEnsure_ReportThePublicOperationThatFailed(string entryPoint, string operation)
    {
        using var harness = new TestHarness();
        var cause = new HttpRequestException("Catalog is unavailable.");
        var catalog = new FaultingCatalog(CatalogStage.List, cause);
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { CollectionCatalog = catalog });
        using var collection = store.GetCollection<string, Article>("article");
        Func<Task> check = entryPoint switch
        {
            "store" => () => store.CollectionExistsAsync("article"),
            "collection" => () => collection.CollectionExistsAsync(),
            _ => () => collection.EnsureCollectionExistsAsync()
        };

        var error = await Assert.ThrowsAsync<VectorStoreException>(check);
        Assert.Equal("vespa", error.VectorStoreSystemName);
        Assert.Equal("article", error.CollectionName);
        Assert.Equal(operation, error.OperationName);
        Assert.Same(cause, error.InnerException);
        Assert.Empty(harness.Handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Listing_DistinguishesCallerCancellationFromCatalogTimeout(bool cancelCaller)
    {
        using var harness = new TestHarness();
        using var caller = new CancellationTokenSource();
        var cause = new TaskCanceledException("Catalog request stopped.", null, caller.Token);
        var catalog = new FaultingCatalog(CatalogStage.MoveNext, cause)
        {
            OnMoveFailure = cancelCaller ? caller.Cancel : null
        };
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { CollectionCatalog = catalog });
        await using var names = store.ListCollectionNamesAsync().GetAsyncEnumerator(caller.Token);
        Assert.True(await names.MoveNextAsync());
        Assert.Equal(caller.Token, catalog.EnumerationToken);

        if (cancelCaller)
            Assert.Same(cause, await Assert.ThrowsAsync<TaskCanceledException>(() => names.MoveNextAsync().AsTask()));
        else
            Assert.Same(cause, (await Assert.ThrowsAsync<VectorStoreException>(() => names.MoveNextAsync().AsTask())).InnerException);
        Assert.Equal(1, catalog.DisposeCalls);
    }

    [Fact]
    public async Task Listing_WithAnAlreadyCanceledEnumeratorToken_DoesNotStartTheCatalog()
    {
        using var harness = new TestHarness();
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var catalog = new FaultingCatalog(CatalogStage.List, new HttpRequestException());
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { CollectionCatalog = catalog });
        await using var names = store.ListCollectionNamesAsync().GetAsyncEnumerator(caller.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => names.MoveNextAsync().AsTask());
        Assert.Equal(0, catalog.ListCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Listing_PreservesExceptionsOutsideTheOperationalContract(bool alreadyContextual)
    {
        using var harness = new TestHarness();
        Exception cause = alreadyContextual ? new VectorStoreException("Custom failure.") : new InvalidOperationException("Invalid state.");
        var catalog = new FaultingCatalog(CatalogStage.MoveNext, cause);
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { CollectionCatalog = catalog });
        await using var names = store.ListCollectionNamesAsync().GetAsyncEnumerator();
        Assert.True(await names.MoveNextAsync());

        Assert.Same(cause, await Record.ExceptionAsync(() => names.MoveNextAsync().AsTask()));
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

    public enum CatalogStage { List, GetEnumerator, Current, MoveNext, Dispose }

    private sealed class FaultingCatalog(CatalogStage stage, Exception cause)
        : IVespaCollectionCatalog, IAsyncEnumerable<string>, IAsyncEnumerator<string>
    {
        public int ListCalls { get; private set; }
        public int MoveCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public CancellationToken EnumerationToken { get; private set; }
        public Action? OnMoveFailure { get; init; }

        public Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromException<bool>(cause);

        public IAsyncEnumerable<string> ListCollectionNamesAsync(CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return stage == CatalogStage.List ? throw cause : this;
        }

        public IAsyncEnumerator<string> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            EnumerationToken = cancellationToken;
            return stage == CatalogStage.GetEnumerator ? throw cause : this;
        }

        public string Current => stage == CatalogStage.Current ? throw cause : "article";

        public ValueTask<bool> MoveNextAsync()
        {
            if (++MoveCalls == 1)
                return ValueTask.FromResult(true);
            if (stage != CatalogStage.MoveNext)
                return ValueTask.FromResult(false);
            OnMoveFailure?.Invoke();
            return ValueTask.FromException<bool>(cause);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return stage == CatalogStage.Dispose ? ValueTask.FromException(cause) : ValueTask.CompletedTask;
        }
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
