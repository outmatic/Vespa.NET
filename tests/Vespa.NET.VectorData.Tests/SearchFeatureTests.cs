using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class SearchFeatureTests
{
    [Theory]
    [InlineData(DistanceFunction.CosineSimilarity, 0, 1, 0.5, true)]
    [InlineData(DistanceFunction.CosineDistance, 0, 0, 0.5, true)]
    [InlineData(DistanceFunction.CosineDistance, Math.PI, 2, 0.5, false)]
    [InlineData(DistanceFunction.DotProductSimilarity, -8, 8, 7, true)]
    [InlineData(DistanceFunction.DotProductSimilarity, 2, -2, -1, false)]
    [InlineData(DistanceFunction.NegativeDotProductSimilarity, -8, -8, -7, true)]
    [InlineData(DistanceFunction.EuclideanDistance, 5, 5, 6, true)]
    [InlineData(DistanceFunction.EuclideanDistance, 5, 5, 4, false)]
    [InlineData(DistanceFunction.EuclideanSquaredDistance, 5, 25, 25, true)]
    [InlineData(DistanceFunction.EuclideanSquaredDistance, 5, 25, 24, false)]
    public async Task Metrics_ExposeNativeScoreAndThresholdDirection(string metric, double distance, double score, double threshold, bool passes)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(Hit(distance)));
        using var collection = harness.Store.GetDynamicCollection("article", Definition(metric));
        var result = Assert.Single(await collection.SearchAsync(new float[] { 1, 0, 0 }, 1).Collect());
        Assert.Equal(score, result.Score!.Value, 10);
        var filtered = await collection.SearchAsync(new float[] { 1, 0, 0 }, 1, new() { ScoreThreshold = threshold }).Collect();
        Assert.Equal(passes ? 1 : 0, filtered.Count);
    }

    [Theory]
    [InlineData(DistanceFunction.CosineSimilarity, -0.1)]
    [InlineData(DistanceFunction.CosineDistance, 4)]
    [InlineData(DistanceFunction.EuclideanDistance, -1)]
    [InlineData(DistanceFunction.EuclideanSquaredDistance, double.MaxValue)]
    public async Task InvalidBackendDistance_FailsContextually(string metric, double distance)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(Hit(distance)));
        using var collection = harness.Store.GetDynamicCollection("article", Definition(metric));
        var error = await Assert.ThrowsAsync<VectorStoreException>(() => collection.SearchAsync(new float[] { 1, 0, 0 }, 1).Collect());
        Assert.Equal("SearchAsync", error.OperationName);
    }

    [Fact]
    public async Task VectorSearch_RejectsObservedRankingOrderThatDisagreesWithDistance()
    {
        using var far = JsonDocument.Parse(Hit(2));
        using var near = JsonDocument.Parse(Hit(1));
        var response = JsonSerializer.Serialize(new
        {
            root = new { children = new[] { far.RootElement.GetProperty("root").GetProperty("children")[0], near.RootElement.GetProperty("root").GetProperty("children")[0] } }
        });
        using var harness = new TestHarness((_, _) => TestHarness.Json(response));
        using var collection = harness.Store.GetDynamicCollection("article", Definition(DistanceFunction.EuclideanDistance));
        var error = await Assert.ThrowsAsync<VectorStoreException>(() => collection.SearchAsync(new float[] { 1, 0, 0 }, 2).Collect());
        Assert.Contains("order results", error.Message);
    }

    [Fact]
    public async Task ZeroVector_IsValidForEuclideanAndEmbeddingInputIsSupported()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(Hit(0)));
        using var collection = harness.Store.GetDynamicCollection("article", Definition(DistanceFunction.EuclideanDistance));
        Assert.Single(await collection.SearchAsync(new Embedding<float>(new float[] { 0, 0, 0 }), 1).Collect());
        using var request = JsonDocument.Parse(harness.Handler.Requests[0].Body!);
        Assert.Equal(0, request.RootElement.GetProperty("input").GetProperty("query(query_embedding)").GetProperty("values")[0].GetSingle());
    }

    [Fact]
    public async Task MultipleVectors_SelectFieldSpecificDimensionsMetricAndQueryBinding()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(Hit(-12, "secondary")));
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with
        {
            VectorFields = new Dictionary<string, VespaVectorFieldOptions>
            {
                ["Second"] = new() { RankProfile = "second_rank", QueryTensorName = "second_query" }
            }
        });
        var definition = Definition(DistanceFunction.CosineSimilarity);
        definition.Properties.Add(new VectorStoreVectorProperty("Second", typeof(float[]), 2)
        { StorageName = "secondary", DistanceFunction = DistanceFunction.DotProductSimilarity });
        using var collection = store.GetDynamicCollection("article", definition);
        var result = Assert.Single(await collection.SearchAsync(new float[] { 1, 2 }, 1,
            new() { VectorProperty = row => row["Second"] }).Collect());
        Assert.Equal(12, result.Score);
        using var request = JsonDocument.Parse(harness.Handler.Requests.Single().Body!);
        Assert.Contains("nearestNeighbor(secondary, second_query)", request.RootElement.GetProperty("yql").GetString());
        Assert.Equal("second_rank", request.RootElement.GetProperty("ranking").GetProperty("profile").GetString());
        Assert.True(request.RootElement.GetProperty("input").TryGetProperty("query(second_query)", out _));
        await Assert.ThrowsAsync<NotSupportedException>(() => collection.SearchAsync(new float[] { 1, 2 }, 1).Collect());
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task MultipleVectors_RequireBindingBeforeSendingQuery()
    {
        using var harness = new TestHarness();
        var definition = Definition(DistanceFunction.CosineSimilarity);
        definition.Properties.Add(new VectorStoreVectorProperty("Second", typeof(float[]), 2));
        using var collection = harness.Store.GetDynamicCollection("article", definition);
        await Assert.ThrowsAsync<NotSupportedException>(() => collection.SearchAsync(new float[] { 1, 2 }, 1,
            new() { VectorProperty = row => row["Second"] }).Collect());
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task Hybrid_CombinesBranchesWithinFilterAndReturnsCombinedScore()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(Hit(0, relevance: 0.42)));
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with
        {
            HybridRankProfile = "hybrid", HybridVectorTargetHits = 40, HybridKeywordTargetHits = 80
        });
        using var collection = store.GetDynamicCollection("article", Definition(DistanceFunction.CosineSimilarity));
        var hybrid = Assert.IsAssignableFrom<IKeywordHybridSearchable<Dictionary<string, object?>>>(collection);
        var results = await hybrid.HybridSearchAsync(new float[] { 1, 0, 0 }, ["vespa", "a\"b\\c"], 3,
            new() { Filter = row => (string)row["Category"]! == "docs", ScoreThreshold = 0.4 }).Collect();
        Assert.Equal(0.42, Assert.Single(results).Score);
        using var request = JsonDocument.Parse(harness.Handler.Requests.Single().Body!);
        var yql = request.RootElement.GetProperty("yql").GetString()!;
        Assert.Contains(") and (({", yql);
        Assert.Contains("({totalTargetHits:40,approximate:false}nearestNeighbor(", yql);
        Assert.Contains(" or ({totalTargetHits:80}weakAnd(", yql);
        Assert.Contains("body contains " + JsonSerializer.Serialize("a\"b\\c"), yql);
        Assert.Equal("hybrid", request.RootElement.GetProperty("ranking").GetProperty("profile").GetString());
        Assert.Empty(await hybrid.HybridSearchAsync(new float[] { 1, 0, 0 }, ["vespa"], 1, new() { ScoreThreshold = 0.5 }).Collect());
    }

    [Theory]
    [InlineData(15, 60)]
    [InlineData(60, 15)]
    public async Task Hybrid_CandidateTargetsAreIndependent(int vectorTarget, int keywordTarget)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(Hit(0)));
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with
        {
            HybridRankProfile = "hybrid", HybridVectorTargetHits = vectorTarget, HybridKeywordTargetHits = keywordTarget
        });
        using var collection = store.GetDynamicCollection("article", Definition(DistanceFunction.CosineSimilarity));
        var hybrid = (IKeywordHybridSearchable<Dictionary<string, object?>>)collection;
        await hybrid.HybridSearchAsync(new float[] { 1, 0, 0 }, ["vespa"], 3, new() { Skip = 2 }).Collect();
        using var request = JsonDocument.Parse(harness.Handler.Requests.Single().Body!);
        var yql = request.RootElement.GetProperty("yql").GetString()!;
        Assert.Contains($"({{totalTargetHits:{vectorTarget},approximate:false}}nearestNeighbor(", yql);
        Assert.Contains($" or ({{totalTargetHits:{keywordTarget}}}weakAnd(", yql);
        Assert.Equal(3, request.RootElement.GetProperty("hits").GetInt32());
        Assert.Equal(2, request.RootElement.GetProperty("offset").GetInt32());
    }

    [Theory]
    [InlineData(null, null, 5, 5)]
    [InlineData(1, 2, 5, 5)]
    [InlineData(1, 60, 5, 60)]
    [InlineData(60, 1, 60, 5)]
    public async Task Hybrid_CandidateTargetsDefaultAndClampIndependentlyToWindow(
        int? vectorTarget, int? keywordTarget, int expectedVector, int expectedKeyword)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(Hit(0)));
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with
        {
            HybridRankProfile = "hybrid", HybridVectorTargetHits = vectorTarget, HybridKeywordTargetHits = keywordTarget
        });
        using var collection = store.GetDynamicCollection("article", Definition(DistanceFunction.CosineSimilarity));
        var hybrid = (IKeywordHybridSearchable<Dictionary<string, object?>>)collection;
        await hybrid.HybridSearchAsync(new float[] { 1, 0, 0 }, ["vespa"], 3, new() { Skip = 2 }).Collect();
        using var request = JsonDocument.Parse(harness.Handler.Requests.Single().Body!);
        var yql = request.RootElement.GetProperty("yql").GetString()!;
        Assert.Contains($"({{totalTargetHits:{expectedVector},approximate:false}}nearestNeighbor(", yql);
        Assert.Contains($" or ({{totalTargetHits:{expectedKeyword}}}weakAnd(", yql);
    }

    [Fact]
    public async Task Hybrid_ValidatesProfileAndFullTextSelectionBeforeIO()
    {
        using var harness = new TestHarness();
        using var collection = harness.Store.GetDynamicCollection("article", Definition(DistanceFunction.CosineSimilarity));
        var hybrid = (IKeywordHybridSearchable<Dictionary<string, object?>>)collection;
        await Assert.ThrowsAsync<NotSupportedException>(() => hybrid.HybridSearchAsync(new float[] { 1, 0, 0 }, ["vespa"], 1).Collect());
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { HybridRankProfile = "hybrid" });
        using var configured = store.GetDynamicCollection("article", Definition(DistanceFunction.CosineSimilarity));
        var configuredHybrid = (IKeywordHybridSearchable<Dictionary<string, object?>>)configured;
        await Assert.ThrowsAsync<NotSupportedException>(() => configuredHybrid.HybridSearchAsync(new float[] { 1, 0, 0 }, ["vespa"], 1,
            new() { AdditionalProperty = row => row["Category"] }).Collect());
        await Assert.ThrowsAsync<ArgumentException>(() => configuredHybrid.HybridSearchAsync(new float[] { 1, 0, 0 }, [], 1).Collect());
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task OrderedRetrieval_UsesStorageNamesMultipleDirectionsAndUnrankedProfile()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(Hit(0)));
        using var collection = harness.Store.GetDynamicCollection("article", Definition(DistanceFunction.CosineSimilarity));
        await collection.GetAsync(row => true, 1, new()
        {
            OrderBy = order => order.Ascending(row => row["Category"]).Descending(row => row["Priority"])
        }).Collect();
        using var request = JsonDocument.Parse(harness.Handler.Requests.Single().Body!);
        Assert.EndsWith("order by category asc, priority desc", request.RootElement.GetProperty("yql").GetString());
        Assert.Equal("unranked", request.RootElement.GetProperty("ranking").GetProperty("profile").GetString());
    }

    [Fact]
    public async Task MultipleStringVectors_UsePropertyGeneratorsAndBoundEachBatch()
    {
        using var firstGenerator = new BatchEmbeddingGenerator();
        using var secondGenerator = new BatchEmbeddingGenerator();
        using var harness = new TestHarness();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with
        {
            EmbeddingGenerator = firstGenerator, UpsertBatchSize = 3, EmbeddingBatchSize = 2, UpsertConcurrency = 1
        });
        var definition = new VectorStoreCollectionDefinition
        {
            Properties =
            [
                new VectorStoreKeyProperty("Id", typeof(string)),
                new VectorStoreVectorProperty("First", typeof(string), 3) { StorageName = "first" },
                new VectorStoreVectorProperty("Second", typeof(string), 3) { StorageName = "second", EmbeddingGenerator = secondGenerator }
            ]
        };
        using var collection = store.GetDynamicCollection("article", definition);
        await collection.UpsertAsync(Enumerable.Range(0, 5).Select(i => new Dictionary<string, object?>
        {
            ["Id"] = "same", ["First"] = "first-" + i, ["Second"] = "second-" + i
        }));
        Assert.Equal(new[] { 2, 1, 2 }, firstGenerator.BatchSizes);
        Assert.Equal(new[] { 2, 1, 2 }, secondGenerator.BatchSizes);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => "first-" + i), firstGenerator.Inputs);
        Assert.Equal(Enumerable.Range(0, 5).Select(i => "second-" + i), secondGenerator.Inputs);
        Assert.Equal(5, harness.Handler.Requests.Count);
        foreach (var request in harness.Handler.Requests)
        {
            using var body = JsonDocument.Parse(request.Body!);
            Assert.Equal(3, body.RootElement.GetProperty("fields").GetProperty("first").GetProperty("values").GetArrayLength());
            Assert.Equal(3, body.RootElement.GetProperty("fields").GetProperty("second").GetProperty("values").GetArrayLength());
        }
    }

    private static VectorStoreCollectionDefinition Definition(string metric) => new()
    {
        Properties =
        [
            new VectorStoreKeyProperty("Id", typeof(string)),
            new VectorStoreDataProperty("Body", typeof(string)) { StorageName = "body", IsFullTextIndexed = true },
            new VectorStoreDataProperty("Category", typeof(string)) { StorageName = "category", IsIndexed = true },
            new VectorStoreDataProperty("Priority", typeof(int)) { StorageName = "priority", IsIndexed = true },
            new VectorStoreVectorProperty("Vector", typeof(float[]), 3) { StorageName = "embedding", DistanceFunction = metric }
        ]
    };

    private static string Hit(double distance, string vector = "embedding", double relevance = 99) => JsonSerializer.Serialize(new
    {
        root = new
        {
            children = new[]
            {
                new
                {
                    id = "index:content/0/synthetic", relevance,
                    fields = new Dictionary<string, object>
                    {
                        ["documentid"] = "id:test:article::one", ["body"] = "Vespa search", ["category"] = "docs", ["priority"] = 1,
                        ["matchfeatures"] = new Dictionary<string, double> { [$"distance(field,{vector})"] = distance }
                    }
                }
            }
        }
    });
}
