using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Models.Schema;
using Vespa.Models.Tensors;
using Vespa.Query;
using Vespa.Testcontainers;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Trait("Category", "E2E")]
public sealed class RankProfileBuilderE2ETests
{
    [Fact]
    public async Task DeploySchema_ExecutesLexicalAndVectorFeaturesAndBothRerankingPhases()
    {
        if (!VespaFixture.IntegrationEnabled) return;

        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        await using var container = new VespaContainer(VespaFixture.Image);
        await container.StartAsync(token);
        var client = container.CreateClient("test");
        var options = new ApplicationPackageOptions()
            .WithRankProfile<Article>("hybrid-score", profile => profile
                .Inherits("default")
                .Function("boost", profile.Attribute(article => article.Popularity) * RankExpression.Reference("factor"), "factor")
                .FirstPhase(profile.Bm25(article => article.Title) + profile.Closeness(article => article.Embedding)))
            .WithRankProfile<Article>("second", profile => profile.Inherits("hybrid-score")
                .SecondPhase(RankExpression.Call("boost", 2) + 10, rerankCount: 10))
            .WithRankProfile<Article>("global", profile => profile.Inherits("second")
                .GlobalPhase(RankExpression.Raw("""
                    if (attribute(popularity_score) > 0,
                        boost(2) + 20,
                        0)
                    """), rerankCount: 10));
        await client.Admin.DeploySchemaAsync<Article>(options, cancellationToken: token);

        while (!await client.IsReadyAsync(token))
            await Task.Delay(500, token);
        if (Environment.GetEnvironmentVariable("VESPA_EXPECTED_VERSION") is { } version)
            Assert.Equal(version, await client.GetVersionAsync(token));

        const string term = "ranking";
        const string queryTensor = "query_vector_embedding";
        var embedding = VespaTensor.FromDenseValues([1f, 2f]);
        var put = await client.Documents.PutAsync("one", new Article
        {
            Title = term,
            Popularity = 21,
            Embedding = embedding
        }, cancellationToken: token);
        Assert.True(put.IsSuccess);

        var lexical = YqlBuilder<Article>.Select().Where(w => w.Field(p => p.Title).Contains(term));
        var vector = YqlBuilder<Article>.Select().Where(w =>
            w.NearestNeighbor(p => p.Embedding, queryTensor, mode: NearestNeighborMode.Exact, totalTargetHits: 5));
        var hybrid = YqlBuilder<Article>.Select().Where(w => w.Rank(
            matching => matching.NearestNeighbor(p => p.Embedding, queryTensor, mode: NearestNeighborMode.Exact, totalTargetHits: 5),
            ranking => ranking.Field(p => p.Title).Contains(term)));

        var lexicalHit = await SearchForHitAsync(lexical.ToSearchRequest().WithRankProfile("hybrid-score"));
        var vectorHit = await SearchForHitAsync(VectorRequest(vector, "hybrid-score"));
        var hybridHit = await SearchForHitAsync(VectorRequest(hybrid, "hybrid-score"));
        Assert.True(lexicalHit.Relevance > 0);
        Assert.True(vectorHit.Relevance > 0);
        Assert.Equal(lexicalHit.Relevance + vectorHit.Relevance, hybridHit.Relevance, precision: 5);
        Assert.Equal(52.0, (await SearchForHitAsync(VectorRequest(hybrid, "second"))).Relevance);
        Assert.Equal(62.0, (await SearchForHitAsync(VectorRequest(hybrid, "global"))).Relevance);

        VespaSearchRequest VectorRequest(YqlBuilder<Article> query, string profile) =>
            query.ToSearchRequest().WithRankProfile(profile).WithQueryTensor(queryTensor, embedding);

        async Task<SearchHit<Article>> SearchForHitAsync(VespaSearchRequest request)
        {
            while (true)
            {
                var result = await client.Search.SearchAsync<Article>(request, cancellationToken: token);
                Assert.Empty(result.Root.Errors ?? []);
                if (result.Root.Children.Count > 0)
                {
                    var hit = Assert.Single(result.Root.Children);
                    Assert.Equal("one", hit.Id);
                    return hit;
                }

                await Task.Delay(500, token);
            }
        }
    }

    [VespaDocument("ranked_article", Namespace = "test")]
    private sealed record Article
    {
        [VespaField(Name = "article_title", IndexingMode = IndexingMode.SummaryIndex, EnableBm25 = true)]
        public string Title { get; init; } = "";

        [VespaField(Name = "popularity_score")]
        public double Popularity { get; init; }

        [VespaField(Name = "vector_embedding")]
        [VespaTensor("tensor<float>(x[2])", DistanceMetric = DistanceMetric.Euclidean)]
        public VespaTensor? Embedding { get; init; }
    }
}
