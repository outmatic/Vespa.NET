using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.VectorData;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class SearchOptimizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilteredRetrieval_DoesNotExecuteApplicationDefaultRanking(bool emptySort)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit()));
        await harness.Collection.GetAsync(row => row.Published, 1,
            emptySort ? new() { OrderBy = order => order } : null).Collect();
        using var query = JsonDocument.Parse(Assert.Single(harness.Handler.Requests).Body!);
        Assert.Equal("unranked", query.RootElement.GetProperty("ranking").GetProperty("profile").GetString());
    }

    [Fact]
    public async Task SearchabilityProbe_DoesNotExecuteApplicationDefaultRanking()
    {
        using var harness = new TestHarness();
        var collection = (VespaVectorStoreCollection<string, Article>)harness.Collection;
        await collection.ValidateSearchabilityAsync();
        await collection.EnsureCollectionExistsAsync();
        Assert.Equal(2, harness.Handler.Requests.Count);
        foreach (var request in harness.Handler.Requests)
        {
            using var query = JsonDocument.Parse(request.Body!);
            Assert.Equal(0, query.RootElement.GetProperty("hits").GetInt32());
            Assert.Equal("unranked", query.RootElement.GetProperty("ranking").GetProperty("profile").GetString());
        }
    }

    [Theory]
    [InlineData(DistanceFunction.DotProductSimilarity, 8, -8)]
    [InlineData(DistanceFunction.DotProductSimilarity, -2, 2)]
    [InlineData(DistanceFunction.DotProductSimilarity, 0, 0)]
    [InlineData(DistanceFunction.NegativeDotProductSimilarity, -8, -8)]
    [InlineData(DistanceFunction.NegativeDotProductSimilarity, 2, 2)]
    public async Task DotThresholds_ArePushedWithInclusiveNativeBoundary(string metric, double threshold, double maximumDistance)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit(distance: maximumDistance)));
        using var collection = harness.Store.GetDynamicCollection("article", Definition(metric));
        Assert.Single(await collection.SearchAsync(new float[] { 1, 0, 0 }, 1,
            new() { ScoreThreshold = threshold }).Collect());
        using var request = JsonDocument.Parse(Assert.Single(harness.Handler.Requests).Body!);
        var yql = request.RootElement.GetProperty("yql").GetString()!;
        var start = yql.IndexOf("distanceThreshold:", StringComparison.Ordinal) + "distanceThreshold:".Length;
        var bound = double.Parse(yql[start..yql.IndexOf('}', start)], CultureInfo.InvariantCulture);
        Assert.True(bound > maximumDistance);
        Assert.Equal(maximumDistance, Math.BitDecrement(bound));
    }

    [Theory]
    [InlineData(DistanceFunction.CosineSimilarity, 1)]
    [InlineData(DistanceFunction.CosineDistance, 0)]
    [InlineData(DistanceFunction.EuclideanDistance, 0)]
    [InlineData(DistanceFunction.EuclideanSquaredDistance, 0)]
    [InlineData(DistanceFunction.DotProductSimilarity, -double.MaxValue)]
    [InlineData(DistanceFunction.NegativeDotProductSimilarity, double.MaxValue)]
    public async Task UnsafeOrUnrepresentableDistanceBounds_KeepTheClientFilter(string metric, double threshold)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit()));
        using var collection = harness.Store.GetDynamicCollection("article", Definition(metric));
        Assert.Single(await collection.SearchAsync(new float[] { 1, 0, 0 }, 1,
            new() { ScoreThreshold = threshold }).Collect());
        using var request = JsonDocument.Parse(Assert.Single(harness.Handler.Requests).Body!);
        Assert.DoesNotContain("distanceThreshold", request.RootElement.GetProperty("yql").GetString());
    }

    [Fact]
    public async Task DotThresholdPushdown_RetainsFinalClientValidation()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit(distance: -7)));
        using var collection = harness.Store.GetDynamicCollection("article", Definition(DistanceFunction.DotProductSimilarity));
        Assert.Empty(await collection.SearchAsync(new float[] { 1, 0, 0 }, 1,
            new() { ScoreThreshold = 8 }).Collect());
    }

    [Fact]
    public async Task HybridCombinedScoreThreshold_IsNeverUsedAsADistanceThreshold()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit()));
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { HybridRankProfile = "hybrid" });
        var definition = Definition(DistanceFunction.DotProductSimilarity);
        definition.Properties.Add(new VectorStoreDataProperty("Text", typeof(string)) { StorageName = "title", IsFullTextIndexed = true });
        using var collection = store.GetDynamicCollection("article", definition);
        var hybrid = (IKeywordHybridSearchable<Dictionary<string, object?>>)collection;
        Assert.Empty(await hybrid.HybridSearchAsync(new float[] { 1, 0, 0 }, ["vespa"], 1,
            new() { ScoreThreshold = 100 }).Collect());
        using var request = JsonDocument.Parse(Assert.Single(harness.Handler.Requests).Body!);
        Assert.DoesNotContain("distanceThreshold", request.RootElement.GetProperty("yql").GetString());
    }

    private static VectorStoreCollectionDefinition Definition(string metric) => new()
    {
        Properties = [new VectorStoreKeyProperty("Id", typeof(string)),
            new VectorStoreVectorProperty("Vector", typeof(ReadOnlyMemory<float>), 3) { StorageName = "embedding", DistanceFunction = metric }]
    };
}
