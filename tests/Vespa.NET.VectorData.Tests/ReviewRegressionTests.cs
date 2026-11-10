using System.Linq.Expressions;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Vespa.Models;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class ReviewRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FloatArrayVectors_RoundTripAndAreClearedWhenExcluded(bool includeVectors)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""
            {"id":"id:test:article::a","fields":{"embedding":{"values":[1,0,0]}}}
            """));
        var collection = harness.Store.GetCollection<string, ArrayRecord>("article");
        var record = await collection.GetAsync("a", new() { IncludeVectors = includeVectors });
        Assert.NotNull(record);
        if (includeVectors)
            Assert.Equal(new float[] { 1, 0, 0 }, record.Embedding);
        else
            Assert.Null(record.Embedding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FloatArraySearch_HonorsVectorProjection(bool includeVectors)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit()));
        var collection = harness.Store.GetCollection<string, ArrayRecord>("article");
        var hit = Assert.Single(await collection.SearchAsync(new float[] { 1, 0, 0 }, 1,
            new() { IncludeVectors = includeVectors }).Collect());
        if (includeVectors)
            Assert.Equal(new float[] { 1, 0, 0 }, hit.Record.Embedding);
        else
            Assert.Null(hit.Record.Embedding);
    }

    [Fact]
    public async Task NullableScalarMapping_PreservesValuesAndMissingFields()
    {
        using var harness = new TestHarness((request, _) => request.Method == HttpMethod.Post
            ? TestHarness.Json("{}") : TestHarness.Json("""
                {"id":"id:test:article::a","fields":{"count":7}}
                """));
        var collection = harness.Store.GetCollection<string, NullableRecord>("article");
        await collection.UpsertAsync(new NullableRecord { Id = "a", Count = 7, Enabled = null, Embedding = new float[] { 1, 0, 0 } });
        using var json = JsonDocument.Parse(harness.Handler.Requests[0].Body!);
        Assert.Equal(7, json.RootElement.GetProperty("fields").GetProperty("count").GetInt32());
        Assert.False(json.RootElement.GetProperty("fields").TryGetProperty("enabled", out _));
        var record = await collection.GetAsync("a");
        Assert.NotNull(record);
        Assert.Equal(7, record.Count);
        Assert.Null(record.Enabled);
    }

    [Fact]
    public async Task NullVectorOnUpsert_IsRejectedBeforeIO()
    {
        using var harness = new TestHarness();
        var collection = harness.Store.GetCollection<string, ArrayRecord>("article");
        await Assert.ThrowsAsync<ArgumentException>(() => collection.UpsertAsync(new ArrayRecord { Id = "a", Embedding = null }));
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task IgnoredPutFields_ReportFailureWithPartialWriteCaveat()
    {
        using var harness = new TestHarness((_, _) =>
        {
            var response = TestHarness.Json("{}");
            response.Headers.Add("X-Vespa-Ignored-Fields", "true");
            return response;
        });
        var error = await Assert.ThrowsAsync<VespaVectorUpsertException>(() => harness.Collection.UpsertAsync(
            new Article { Id = "a", Embedding = new float[] { 1, 0, 0 } }));
        AssertContext(error, "UpsertAsync");
        Assert.Contains("partially applied", error.Message);
        Assert.Single(harness.Handler.Requests);
    }

    [Theory]
    [InlineData("upsert", "UpsertAsync")]
    [InlineData("get", "GetAsync")]
    [InlineData("delete", "DeleteAsync")]
    [InlineData("search", "SearchAsync")]
    [InlineData("filtered-get", "GetAsync")]
    public async Task BackendFailures_UseTheStandardExceptionContract(string operation, string expectedOperation)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("{\"message\":\"backend unavailable\"}", HttpStatusCode.ServiceUnavailable));
        var collection = harness.Collection;
        Func<Task> action = operation switch
        {
            "upsert" => () => collection.UpsertAsync(new Article { Id = "a", Embedding = new float[] { 1, 0, 0 } }),
            "get" => () => collection.GetAsync("a"),
            "delete" => () => collection.DeleteAsync("a"),
            "search" => () => collection.SearchAsync(new float[] { 1, 0, 0 }, 1).Collect(),
            _ => () => collection.GetAsync(a => a.Published, 1).Collect()
        };
        var error = await Assert.ThrowsAnyAsync<VectorStoreException>(action);
        AssertContext(error, expectedOperation);
        Assert.IsAssignableFrom<VespaException>(error.InnerException);
    }

    [Fact]
    public async Task NonCallerTimeout_IsAnOperationalFailure()
    {
        using var harness = new TestHarness((_, _) => throw new TaskCanceledException("Backend timed out."));
        var error = await Assert.ThrowsAsync<VectorStoreException>(() => harness.Collection.SearchAsync(new float[] { 1, 0, 0 }, 1).Collect());
        AssertContext(error, "SearchAsync");
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
    }

    [Fact]
    public async Task MalformedStoredVector_IsAContextualReadFailure()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""
            {"id":"id:test:article::a","fields":{"embedding":{"values":[1]}}}
            """));
        var error = await Assert.ThrowsAsync<VectorStoreException>(() => harness.Collection.GetAsync("a", new() { IncludeVectors = true }));
        AssertContext(error, "GetAsync");
        Assert.NotNull(error.InnerException);
    }

    [Theory]
    [InlineData("{\"values\":null}")]
    [InlineData("{\"values\":[0,0,0]}")]
    [InlineData("{\"values\":[1,\"bad\",0]}")]
    [InlineData("{\"cells\":[]}")]
    public async Task MalformedTensorJSON_UsesContextualErrors(string tensor)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(
            "{\"id\":\"id:test:article::a\",\"fields\":{\"embedding\":" + tensor + "}}"));
        var error = await Assert.ThrowsAsync<VectorStoreException>(() => harness.Collection.GetAsync("a", new() { IncludeVectors = true }));
        AssertContext(error, "GetAsync");
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public async Task InvalidTextQueryFilter_DoesNotInvokeEmbeddings()
    {
        using var harness = new TestHarness();
        using var embeddings = new FixedEmbeddingGenerator();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = embeddings });
        await Assert.ThrowsAsync<NotSupportedException>(() => store.GetCollection<string, Article>("article")
            .SearchAsync("question", 1, new() { Filter = a => a.Title.StartsWith("A") }).Collect());
        Assert.Equal(0, embeddings.Calls);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task CancelledTextQuery_DoesNotInvokeEmbeddingsOrHTTP()
    {
        using var harness = new TestHarness();
        using var embeddings = new FixedEmbeddingGenerator();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = embeddings });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetCollection<string, Article>("article")
            .SearchAsync("question", 1, cancellationToken: cancelled.Token).Collect());
        Assert.Equal(0, embeddings.Calls);
        Assert.Empty(harness.Handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompatibleExplicitGenerator_IsRejectedInsteadOfUsingFallback(bool onProperty)
    {
        using var harness = new TestHarness();
        using var fallback = new FixedEmbeddingGenerator();
        using var incompatible = new DoubleEmbeddingGenerator();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = fallback });
        var vector = new VectorStoreVectorProperty(nameof(Article.Embedding), 3) { StorageName = "embedding" };
        var definition = new VectorStoreCollectionDefinition
        {
            Properties = [new VectorStoreKeyProperty(nameof(Article.Id), typeof(string)), vector]
        };
        if (onProperty)
            vector.EmbeddingGenerator = incompatible;
        else
            definition.EmbeddingGenerator = incompatible;
        Assert.Throws<NotSupportedException>(() => store.GetCollection<string, Article>("article", definition));
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task MeaningChangingCasts_AreRejected()
    {
        using var harness = new TestHarness();
        var minimum = 1.5;
        Expression<Func<Article, bool>>[] filters =
        [
            a => (byte)a.Priority == 1,
            a => checked((byte)a.Priority) == 1,
            a => (float)a.Priority == 16777216f,
            a => a.Priority == (int)minimum
        ];
        foreach (var filter in filters)
            await Assert.ThrowsAsync<NotSupportedException>(() => harness.Collection.SearchAsync(
                new float[] { 1, 0, 0 }, 1, new() { Filter = filter }).Collect());
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task PromotedFloatConstant_PreservesItsExactDoubleValue()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("{\"root\":{\"children\":[]}}"));
        var threshold = 0.1f;
        var collection = harness.Store.GetCollection<string, ArrayRecord>("article");
        await collection.SearchAsync(new float[] { 1, 0, 0 }, 1, new() { Filter = a => a.Ratio == (double)threshold }).Collect();
        using var json = JsonDocument.Parse(Assert.Single(harness.Handler.Requests).Body!);
        Assert.Contains("ratio = 0.10000000149011612", json.RootElement.GetProperty("yql").GetString());
    }

    [Fact]
    public async Task NumericAndBooleanInequality_UseCanonicalNegatedEquality()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("{\"root\":{\"children\":[]}}"));
        await harness.Collection.SearchAsync(new float[] { 1, 0, 0 }, 1,
            new() { Filter = a => a.Priority != 2 && a.Published != true }).Collect();
        using var json = JsonDocument.Parse(Assert.Single(harness.Handler.Requests).Body!);
        var yql = json.RootElement.GetProperty("yql").GetString();
        Assert.Contains("!(priority = 2)", yql);
        Assert.Contains("!(published = true)", yql);
        Assert.DoesNotContain("!=", yql);
    }

    [Fact]
    public async Task ArraysAndEmptyStrings_AreNotMisrepresentedAsScalarTerms()
    {
        using var harness = new TestHarness();
        var collection = harness.Store.GetCollection<string, ArrayRecord>("article");
        var tags = new[] { "a" };
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => collection.SearchAsync(new float[] { 1, 0, 0 }, 1,
            new() { Filter = a => a.Tags == tags }).Collect());
        Assert.Contains("scalar", error.Message);
        await Assert.ThrowsAsync<NotSupportedException>(() => harness.Collection.SearchAsync(new float[] { 1, 0, 0 }, 1,
            new() { Filter = a => a.Category == "" }).Collect());
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public void DuplicatePropertyMappings_AreRejected()
    {
        using var harness = new TestHarness();
        var definition = new VectorStoreCollectionDefinition
        {
            Properties =
            [
                new VectorStoreKeyProperty(nameof(Article.Id), typeof(string)),
                new VectorStoreDataProperty(nameof(Article.Id), typeof(string)) { StorageName = "other_id" },
                new VectorStoreVectorProperty(nameof(Article.Embedding), 3) { StorageName = "embedding" }
            ]
        };
        Assert.Throws<ArgumentException>(() => harness.Store.GetCollection<string, Article>("article", definition));
    }

    private static void AssertContext(VectorStoreException error, string operation)
    {
        Assert.Equal("vespa", error.VectorStoreSystemName);
        Assert.Equal("article", error.CollectionName);
        Assert.Equal(operation, error.OperationName);
    }
}

public sealed class ArrayRecord
{
    [VectorStoreKey]
    public string Id { get; set; } = "";
    [VectorStoreVector(3, StorageName = "embedding")]
    public float[]? Embedding { get; set; } = [1, 0, 0];
    [VectorStoreData(StorageName = "ratio", IsIndexed = true)]
    public double Ratio { get; set; }
    [VectorStoreData(StorageName = "tags", IsIndexed = true)]
    public string[] Tags { get; set; } = [];
}

public sealed class NullableRecord
{
    [VectorStoreKey]
    public string Id { get; set; } = "";
    [VectorStoreData(StorageName = "count")]
    public int? Count { get; set; }
    [VectorStoreData(StorageName = "enabled")]
    public bool? Enabled { get; set; }
    [VectorStoreVector(3, StorageName = "embedding")]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

internal sealed class DoubleEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<double>>
{
    public Task<GeneratedEmbeddings<Embedding<double>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
