using System.Text.Json;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class QueryConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchAndFilteredRetrieval_SelectIndependentSummaryClasses(bool includeVectors)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit()));
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with
        {
            SummaryClass = "data-only",
            VectorSummaryClass = "data-with-vectors"
        });
        using var collection = store.GetCollection<string, Article>("article");
        await collection.SearchAsync(new float[] { 1, 0, 0 }, 1, new() { IncludeVectors = includeVectors }).Collect();
        await collection.GetAsync(row => row.Published, 1, new() { IncludeVectors = includeVectors }).Collect();
        Assert.Equal(2, harness.Handler.Requests.Count);
        foreach (var request in harness.Handler.Requests)
        {
            using var body = JsonDocument.Parse(request.Body!);
            Assert.Equal(includeVectors ? "data-with-vectors" : "data-only", body.RootElement.GetProperty("presentation.summary").GetString());
            var projection = body.RootElement.GetProperty("yql").GetString()!.Split(" from ", 2)[0];
            Assert.Equal(includeVectors, projection.Contains("embedding", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task LightSummary_DoesNotAffectRequestsForVectorsOrKeyBasedReads()
    {
        using var harness = new TestHarness((request, _) => TestHarness.Json(request.Method == HttpMethod.Get
            ? """{"fields":{"embedding":{"values":[1,0,0]}}}""" : TestHarness.Hit()));
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { SummaryClass = "vector_data" });
        using var collection = store.GetCollection<string, Article>("article");
        var results = await collection.SearchAsync(new float[] { 1, 0, 0 }, 1, new() { IncludeVectors = true }).Collect();
        Assert.Equal(3, Assert.Single(results).Record.Embedding.Length);
        using var body = JsonDocument.Parse(Assert.Single(harness.Handler.Requests).Body!);
        Assert.False(body.RootElement.TryGetProperty("presentation.summary", out _));
        await collection.GetAsync("a:1", new() { IncludeVectors = true });
        Assert.Contains("/document/v1/", harness.Handler.Requests[1].Uri.AbsolutePath);
        Assert.DoesNotContain("summary", harness.Handler.Requests[1].Uri.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultSearchAndFilteredReads_LeaveSummaryAndExplorationToVespa(bool includeVectors)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit()));
        using var store = new VespaVectorStore(harness.Client, new() { Namespace = "test", RankProfile = "vector_search" });
        using var collection = store.GetCollection<string, Article>("article");
        await collection.SearchAsync(new float[] { 1, 0, 0 }, 1, new() { IncludeVectors = includeVectors }).Collect();
        await collection.GetAsync(row => row.Published, 1, new() { IncludeVectors = includeVectors }).Collect();
        Assert.Equal(2, harness.Handler.Requests.Count);
        foreach (var request in harness.Handler.Requests)
        {
            using var body = JsonDocument.Parse(request.Body!);
            Assert.False(body.RootElement.TryGetProperty("presentation.summary", out _));
            Assert.DoesNotContain("hnsw.exploreAdditionalHits", body.RootElement.GetProperty("yql").GetString());
        }
        using var searchBody = JsonDocument.Parse(harness.Handler.Requests[0].Body!);
        Assert.Contains("approximate:true", searchBody.RootElement.GetProperty("yql").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HnswExploration_IsAppliedOnlyToApproximateSearch(bool approximate)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit()));
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with
        {
            Approximate = approximate,
            HnswExploreAdditionalHits = 64
        });
        using var collection = store.GetCollection<string, Article>("article");
        await collection.SearchAsync(new float[] { 1, 0, 0 }, 2, new() { Skip = 3 }).Collect();
        using var body = JsonDocument.Parse(Assert.Single(harness.Handler.Requests).Body!);
        var yql = body.RootElement.GetProperty("yql").GetString()!;
        Assert.Contains("totalTargetHits:5", yql);
        Assert.Equal(approximate, yql.Contains("hnsw.exploreAdditionalHits:64", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidQueryConfiguration_FailsBeforeIO()
    {
        using var harness = new TestHarness();
        Assert.Throws<ArgumentException>(() => new VespaVectorStore(harness.Client, TestHarness.Options with { SummaryClass = " " }));
        Assert.Throws<ArgumentException>(() => new VespaVectorStore(harness.Client, TestHarness.Options with { VectorSummaryClass = "" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VespaVectorStore(harness.Client, TestHarness.Options with { HnswExploreAdditionalHits = -1 }));
        Assert.Empty(harness.Handler.Requests);
    }
}
