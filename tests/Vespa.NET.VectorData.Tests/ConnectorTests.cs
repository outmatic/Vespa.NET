using System.Globalization;
using System.Linq.Expressions;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.VectorData;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class ConnectorTests
{
    [Fact]
    public async Task Upsert_UsesDocumentIdentityAndDenseTensorWithMappedStorageNames()
    {
        using var harness = new TestHarness();
        await harness.Collection.UpsertAsync(new Article { Id = "id:opaque:article::a/b", Title = "A", Embedding = new float[] { 1, 0, 0 } });
        var request = Assert.Single(harness.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/document/v1/test/article/docid/id%3Aopaque%3Aarticle%3A%3Aa%2Fb", request.Uri.AbsoluteUri);
        using var json = JsonDocument.Parse(request.Body!);
        var fields = json.RootElement.GetProperty("fields");
        Assert.Equal("A", fields.GetProperty("title").GetString());
        Assert.False(fields.TryGetProperty("Id", out _));
        Assert.False(fields.TryGetProperty("id", out _));
        Assert.Equal(3, fields.GetProperty("embedding").GetProperty("values").GetArrayLength());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_MapsEnvelopeKeyAndHonorsIncludeVectors(bool includeVectors)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""
            {"id":"id:test:article::a:1","fields":{"title":"A","embedding":{"type":"tensor<float>(x[3])","values":[1,0,0]}}}
            """));
        var record = await harness.Collection.GetAsync("a:1", new() { IncludeVectors = includeVectors });
        Assert.NotNull(record);
        Assert.Equal("a:1", record.Id);
        Assert.Equal("A", record.Title);
        Assert.Equal(includeVectors ? 3 : 0, record.Embedding.Length);
    }

    [Fact]
    public async Task Get_MissingDocument_ReturnsNull()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("{}", HttpStatusCode.NotFound));
        Assert.Null(await harness.Collection.GetAsync("missing"));
    }

    [Fact]
    public async Task Delete_UsesConfiguredNamespaceAndSchema()
    {
        using var harness = new TestHarness();
        await harness.Collection.DeleteAsync("a:1");
        var request = Assert.Single(harness.Handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.EndsWith("/document/v1/test/article/docid/a%3A1", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task VectorSearch_UsesCorrectInputAndReturnsCosineInsteadOfArbitraryRankScore()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit(distance: Math.PI / 3)));
        var results = await harness.Collection.SearchAsync(new float[] { 1, 0, 0 }, 2, new() { Skip = 3 }).Collect();
        var result = Assert.Single(results);
        Assert.Equal("a:1", result.Record.Id);
        Assert.Equal(0.5, result.Score!.Value, 6);
        using var json = JsonDocument.Parse(Assert.Single(harness.Handler.Requests).Body!);
        Assert.Equal("article", json.RootElement.GetProperty("model.restrict").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("offset").GetInt32());
        Assert.Equal("5000ms", json.RootElement.GetProperty("timeout").GetString());
        Assert.Contains("totalTargetHits:5,approximate:false", json.RootElement.GetProperty("yql").GetString());
        Assert.Contains("documentid", json.RootElement.GetProperty("yql").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("input").GetProperty("query(query_embedding)").GetProperty("values").GetArrayLength());
        Assert.Empty(result.Record.Embedding.ToArray());
    }

    [Fact]
    public async Task Search_ThresholdFiltersByCosine()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit(distance: Math.PI / 2)));
        Assert.Empty(await harness.Collection.SearchAsync(new float[] { 1, 0, 0 }, 2, new() { ScoreThreshold = 0.5 }).Collect());
    }

    [Fact]
    public async Task Filter_UsesStorageNamesCapturedValuesAndEscapesStrings()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("{\"root\":{\"children\":[]}}"));
        var category = "docs\" or true or title contains \"x";
        var minimum = 2;
        await harness.Collection.SearchAsync(new float[] { 1, 0, 0 }, 2,
            new() { Filter = a => a.Category == category && a.Priority >= minimum && a.Published }).Collect();
        using var json = JsonDocument.Parse(Assert.Single(harness.Handler.Requests).Body!);
        var yql = json.RootElement.GetProperty("yql").GetString()!;
        Assert.Contains("priority >= 2", yql);
        Assert.Contains("published = true", yql);
        Assert.Contains(JsonSerializer.Serialize(category), yql);
        Assert.DoesNotContain("Category", yql);
    }

    [Fact]
    public async Task Filter_UsesInvariantNumbersAndSupportsReversedComparison()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("{\"root\":{\"children\":[]}}"));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");
            await harness.Collection.SearchAsync(new float[] { 1, 0, 0 }, 1, new() { Filter = a => 1.5 < a.Priority }).Collect();
            using var json = JsonDocument.Parse(Assert.Single(harness.Handler.Requests).Body!);
            Assert.Contains("priority > 1.5", json.RootElement.GetProperty("yql").GetString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task UnsupportedFilter_FailsBeforeSendingAnything()
    {
        using var harness = new TestHarness();
        await Assert.ThrowsAsync<NotSupportedException>(() => harness.Collection.SearchAsync(
            new float[] { 1, 0, 0 }, 1, new() { Filter = a => a.Title.StartsWith("A") }).Collect());
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task FilterOnUnindexedProperty_IsRejected()
    {
        using var harness = new TestHarness();
        await Assert.ThrowsAsync<NotSupportedException>(() => harness.Collection.SearchAsync(
            new float[] { 1, 0, 0 }, 1, new() { Filter = a => a.Title == "A" }).Collect());
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task NullFilter_FailsExplicitly()
    {
        using var harness = new TestHarness();
        await Assert.ThrowsAsync<NotSupportedException>(() => harness.Collection.SearchAsync(
            new float[] { 1, 0, 0 }, 1, new() { Filter = a => a.Title == null }).Collect());
        Assert.Empty(harness.Handler.Requests);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task DimensionMismatch_FailsBeforeIO(int size)
    {
        using var harness = new TestHarness();
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Collection.SearchAsync(Enumerable.Repeat(1f, size).ToArray(), 1).Collect());
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task ZeroAndNonfiniteVectors_AreRejected()
    {
        using var harness = new TestHarness();
        foreach (var vector in new[] { new float[] { 0, 0, 0 }, new float[] { float.NaN, 0, 1 }, new float[] { float.PositiveInfinity, 0, 1 } })
            await Assert.ThrowsAsync<ArgumentException>(() => harness.Collection.SearchAsync(vector, 1).Collect());
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task WrongVectorSelector_IsRejected()
    {
        using var harness = new TestHarness();
        await Assert.ThrowsAsync<NotSupportedException>(() => harness.Collection.SearchAsync(new float[] { 1, 0, 0 }, 1,
            new() { VectorProperty = a => a.Title }).Collect());
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task SearchWindow_IsBoundedWithoutOverflow()
    {
        using var harness = new TestHarness();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Collection.SearchAsync(new float[] { 1, 0, 0 }, int.MaxValue,
            new() { Skip = int.MaxValue }).Collect());
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task PartialErrorsInSuccessfulHTTPResponse_AreNotSilentlyIgnored()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""
            {"root":{"errors":[{"code":4,"message":"Query timed out"}],"children":[]}}
            """));
        await Assert.ThrowsAsync<VectorStoreException>(() => harness.Collection.SearchAsync(new float[] { 1, 0, 0 }, 1).Collect());
    }

    [Fact]
    public async Task IncompleteCoverage_IsRejected()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""
            {"root":{"coverage":{"full":false,"coverage":50},"children":[]}}
            """));
        await Assert.ThrowsAsync<VectorStoreException>(() => harness.Collection.SearchAsync(new float[] { 1, 0, 0 }, 1).Collect());
    }

    [Theory]
    [InlineData("id:other:article::a")]
    [InlineData("id:test:other::a")]
    [InlineData("id:test:article:g=group:a")]
    public async Task SearchCannotReturnKeysFromAnotherNamespaceOrAddressingMode(string id)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit(id)));
        await Assert.ThrowsAsync<VectorStoreException>(() => harness.Collection.SearchAsync(new float[] { 1, 0, 0 }, 1).Collect());
    }

    [Fact]
    public async Task MissingDistanceFeature_IsRejected()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""
            {"root":{"children":[{"id":"id:test:article::a","fields":{"title":"A"}}]}}
            """));
        var error = await Assert.ThrowsAsync<VectorStoreException>(() => harness.Collection.SearchAsync(new float[] { 1, 0, 0 }, 1).Collect());
        Assert.Contains("match-feature", error.Message);
    }

    [Fact]
    public async Task CancelledEnumeration_PropagatesCancellation()
    {
        using var harness = new TestHarness();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Collection.SearchAsync(
            new float[] { 1, 0, 0 }, 1, cancellationToken: cancellation.Token).Collect());
    }

    [Fact]
    public async Task DiscoveryAndDestructiveLifecycleOperations_RemainUnsupported()
    {
        using var harness = new TestHarness();
        Assert.Throws<NotSupportedException>(() => harness.Store.ListCollectionNamesAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => harness.Store.CollectionExistsAsync("article"));
        await Assert.ThrowsAsync<NotSupportedException>(() => harness.Collection.EnsureCollectionDeletedAsync());
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public void UnsupportedKeysAndUnsafeIdentifiers_FailEarly()
    {
        using var harness = new TestHarness();
        Assert.Throws<NotSupportedException>(() => harness.Store.GetCollection<int, Article>("article"));
        Assert.Throws<ArgumentException>(() => harness.Store.GetCollection<string, Article>("article where true"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VespaVectorStore(harness.Client,
            TestHarness.Options with { SearchTimeout = TimeSpan.Zero }));
    }

    [Fact]
    public void NestedDataModels_AreRejectedInsteadOfSilentlyLosingFieldNames()
    {
        using var harness = new TestHarness();
        Assert.Throws<NotSupportedException>(() => harness.Store.GetCollection<string, NestedRecord>("article"));
    }

    [Fact]
    public async Task StoreAndCollection_DoNotOwnTheSuppliedClient()
    {
        using var harness = new TestHarness();
        var collection = harness.Collection;
        collection.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => collection.GetAsync("a"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => collection.UpsertAsync(Array.Empty<Article>()));
        harness.Store.Dispose();
        Assert.False(harness.Handler.Disposed);
        Assert.Throws<ObjectDisposedException>(() => harness.Store.GetCollection<string, Article>("article"));
    }

    [Fact]
    public async Task ExplicitDefinition_WorksWithoutDependingOnVespaModelAttributes()
    {
        using var harness = new TestHarness();
        var definition = new VectorStoreCollectionDefinition
        {
            Properties =
            [
                new VectorStoreKeyProperty(nameof(Article.Id), typeof(string)),
                new VectorStoreDataProperty(nameof(Article.Title), typeof(string)) { StorageName = "title" },
                new VectorStoreVectorProperty(nameof(Article.Embedding), 3)
                { StorageName = "embedding", DistanceFunction = DistanceFunction.CosineSimilarity }
            ]
        };
        var collection = harness.Store.GetCollection<string, Article>("article", definition);
        await collection.UpsertAsync(new Article { Id = "a", Title = "A", Embedding = new float[] { 1, 0, 0 } });
        Assert.Contains("\"title\":\"A\"", Assert.Single(harness.Handler.Requests).Body!);
    }
}

public sealed class NestedRecord
{
    [VectorStoreKey]
    public string Id { get; set; } = "";
    [VectorStoreData]
    public Article? Nested { get; set; }
    [VectorStoreVector(3)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
