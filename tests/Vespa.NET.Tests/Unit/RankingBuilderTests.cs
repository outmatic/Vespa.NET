using System.Text.Json;
using Vespa.Models;
using Vespa.Query;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for RankingBuilder: fluent construction, serialization, and extension methods
/// </summary>
public class RankingBuilderTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // --- Profile ---

    [Fact]
    public void WithProfile_SetsProfileName()
    {
        var config = RankingBuilder.WithProfile("semantic").Build();
        Assert.Equal("semantic", config.Profile);
    }

    [Fact]
    public void Create_ThenProfile_SetsProfileName()
    {
        var config = RankingBuilder.Create().Profile("bm25").Build();
        Assert.Equal("bm25", config.Profile);
    }

    [Fact]
    public void WithProfile_NoOtherFields_ReturnsMinimalConfig()
    {
        var config = RankingBuilder.WithProfile("default").Build();
        Assert.Null(config.Features);
        Assert.Null(config.MatchFeatures);
        Assert.Null(config.RerankCount);
        Assert.Null(config.ListFeatures);
        Assert.Null(config.Properties);
    }

    // --- Features ---

    [Fact]
    public void Feature_ScalarDouble_SetInFeaturesDict()
    {
        var config = RankingBuilder.WithProfile("p")
            .Feature("query(threshold)", 0.8)
            .Build();

        Assert.NotNull(config.Features);
        Assert.Equal(0.8, config.Features["query(threshold)"]);
    }

    [Fact]
    public void Feature_MultipleFeatures_AllPresent()
    {
        var config = RankingBuilder.Create()
            .Feature("query(a)", 1.0)
            .Feature("query(b)", 2.0)
            .Build();

        Assert.Equal(2, config.Features!.Count);
        Assert.Equal(1.0, config.Features["query(a)"]);
        Assert.Equal(2.0, config.Features["query(b)"]);
    }

    [Fact]
    public void Feature_OverwritesSameKey()
    {
        var config = RankingBuilder.Create()
            .Feature("query(x)", 1.0)
            .Feature("query(x)", 99.0)
            .Build();

        Assert.Equal(99.0, config.Features!["query(x)"]);
    }

    [Fact]
    public void Feature_NoFeatures_FeaturesIsNull()
    {
        var config = RankingBuilder.WithProfile("p").Build();
        Assert.Null(config.Features);
    }

    [Fact]
    public void Feature_ObjectValue_StoredAsObject()
    {
        var tensor = new[] { 0.1, 0.2, 0.3 };
        var config = RankingBuilder.Create().Feature("query(embedding)", tensor).Build();
        Assert.Same(tensor, config.Features!["query(embedding)"]);
    }

    // --- MatchFeatures ---

    [Fact]
    public void MatchFeature_Single_SetsMatchFeaturesString()
    {
        var config = RankingBuilder.Create().MatchFeature("bm25(title)").Build();
        Assert.Equal("bm25(title)", config.MatchFeatures);
    }

    [Fact]
    public void MatchFeature_Multiple_JoinedWithSpace()
    {
        var config = RankingBuilder.Create()
            .MatchFeature("bm25(title)")
            .MatchFeature("closeness(field, embedding)")
            .Build();

        Assert.Equal("bm25(title) closeness(field, embedding)", config.MatchFeatures);
    }

    [Fact]
    public void MatchFeatures_ParamsOverload_AllJoined()
    {
        var config = RankingBuilder.Create()
            .MatchFeatures("bm25(title)", "nativeFieldMatch(body)", "closeness(field, embedding)")
            .Build();

        Assert.Equal("bm25(title) nativeFieldMatch(body) closeness(field, embedding)", config.MatchFeatures);
    }

    [Fact]
    public void MatchFeature_None_IsNull()
    {
        var config = RankingBuilder.WithProfile("p").Build();
        Assert.Null(config.MatchFeatures);
    }

    [Fact]
    public void MatchFeatures_MixSingleAndParams_AllPresent()
    {
        var config = RankingBuilder.Create()
            .MatchFeature("bm25(title)")
            .MatchFeatures("closeness(field, embedding)", "nativeRank")
            .Build();

        // Feature names can contain spaces (e.g. "closeness(field, embedding)")
        // so verify by substring containment, not by splitting on space
        Assert.Contains("bm25(title)", config.MatchFeatures);
        Assert.Contains("closeness(field, embedding)", config.MatchFeatures);
        Assert.Contains("nativeRank", config.MatchFeatures);
    }

    // --- RerankCount ---

    [Fact]
    public void RerankCount_SetsValue()
    {
        var config = RankingBuilder.WithProfile("p").RerankCount(200).Build();
        Assert.Equal(200, config.RerankCount);
    }

    [Fact]
    public void RerankCount_NotSet_IsNull()
    {
        var config = RankingBuilder.WithProfile("p").Build();
        Assert.Null(config.RerankCount);
    }

    // --- ListAllFeatures ---

    [Fact]
    public void ListAllFeatures_DefaultTrue()
    {
        var config = RankingBuilder.Create().ListAllFeatures().Build();
        Assert.True(config.ListFeatures);
    }

    [Fact]
    public void ListAllFeatures_ExplicitFalse()
    {
        var config = RankingBuilder.Create().ListAllFeatures(false).Build();
        Assert.False(config.ListFeatures);
    }

    [Fact]
    public void ListAllFeatures_NotCalled_IsNull()
    {
        var config = RankingBuilder.Create().Build();
        Assert.Null(config.ListFeatures);
    }

    // --- Property ---

    [Fact]
    public void Property_SetsRankProperty()
    {
        var config = RankingBuilder.Create().Property("vespa.matching.numthreadsperSearch", 4).Build();
        Assert.NotNull(config.Properties);
        Assert.Equal(4, config.Properties["vespa.matching.numthreadsperSearch"]);
    }

    [Fact]
    public void Property_None_IsNull()
    {
        var config = RankingBuilder.Create().Build();
        Assert.Null(config.Properties);
    }

    // --- Full pipeline ---

    [Fact]
    public void Build_FullConfig_AllFieldsSet()
    {
        var config = RankingBuilder
            .WithProfile("semantic")
            .Feature("query(threshold)", 0.7)
            .MatchFeatures("bm25(title)", "closeness(field, embedding)")
            .RerankCount(100)
            .ListAllFeatures(false)
            .Build();

        Assert.Equal("semantic", config.Profile);
        Assert.Equal(0.7, config.Features!["query(threshold)"]);
        Assert.Equal("bm25(title) closeness(field, embedding)", config.MatchFeatures);
        Assert.Equal(100, config.RerankCount);
        Assert.False(config.ListFeatures);
    }

    // --- Implicit conversion operator ---

    [Fact]
    public void ImplicitConversion_ToRankingConfig()
    {
        RankingConfig config = RankingBuilder.WithProfile("bm25");
        Assert.Equal("bm25", config.Profile);
    }

    // --- WithRanking extension ---

    [Fact]
    public void WithRanking_SetsRankingOnRequest()
    {
        var request = new VespaSearchRequest { Yql = "select * from music;" };
        request.WithRanking(RankingBuilder.WithProfile("semantic").RerankCount(50));

        Assert.NotNull(request.Ranking);
        Assert.Equal("semantic", request.Ranking!.Profile);
        Assert.Equal(50, request.Ranking.RerankCount);
    }

    [Fact]
    public void WithRanking_ReturnsRequest_ForChaining()
    {
        var request = new VespaSearchRequest { Yql = "select * from music;" };
        var returned = request.WithRanking(RankingBuilder.WithProfile("p"));
        Assert.Same(request, returned);
    }

    // --- JSON serialization ---

    [Fact]
    public void Serialization_Profile_EmitsProfileField()
    {
        var request = new VespaSearchRequest
        {
            Yql = "select * from music;",
            Ranking = RankingBuilder.WithProfile("semantic")
        };

        var json = JsonSerializer.Serialize(request, JsonOpts);
        Assert.Contains(@"""profile"":""semantic""", json);
    }

    [Fact]
    public void Serialization_MatchFeatures_EmitsMatchFeaturesField()
    {
        var config = RankingBuilder.Create()
            .MatchFeatures("bm25(title)", "closeness(field, embedding)")
            .Build();

        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains("matchFeatures", json);
        Assert.Contains("bm25(title)", json);
    }

    [Fact]
    public void Serialization_RerankCount_EmitsField()
    {
        var config = RankingBuilder.Create().RerankCount(200).Build();
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains(@"""rerankCount"":200", json);
    }

    [Fact]
    public void Serialization_NullFields_NotEmitted()
    {
        var config = RankingBuilder.WithProfile("p").Build();
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.DoesNotContain("matchFeatures", json);
        Assert.DoesNotContain("rerankCount", json);
        Assert.DoesNotContain("features", json);
    }

    [Fact]
    public void Serialization_Features_EmittedUnderFeaturesKey()
    {
        var config = RankingBuilder.Create()
            .Feature("query(threshold)", 0.8)
            .Build();

        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains(@"""features""", json);
        Assert.Contains("query(threshold)", json);
    }

    // --- Immutability: Build creates new dict copies ---

    [Fact]
    public void Build_CalledTwice_ReturnsSeparateInstances()
    {
        var builder = RankingBuilder.WithProfile("p").Feature("query(x)", 1.0);
        var c1 = builder.Build();
        var c2 = builder.Build();
        Assert.NotSame(c1, c2);
        Assert.NotSame(c1.Features, c2.Features);
    }

    // --- M8: New RankingBuilder methods ---

    [Fact]
    public void Sorting_SetsSortingSpec()
    {
        var config = RankingBuilder.WithProfile("p").Sorting("+year -relevance").Build();
        Assert.Equal("+year -relevance", config.Sorting);
    }

    [Fact]
    public void SoftTimeout_SetsEnableAndFactor()
    {
        var config = RankingBuilder.WithProfile("p").SoftTimeout(true, 0.6).Build();
        Assert.True(config.SoftTimeoutEnable);
        Assert.Equal(0.6, config.SoftTimeoutFactor);
    }

    [Fact]
    public void SoftTimeout_EnableOnly()
    {
        var config = RankingBuilder.WithProfile("p").SoftTimeout().Build();
        Assert.True(config.SoftTimeoutEnable);
        Assert.Null(config.SoftTimeoutFactor);
    }

    [Fact]
    public void Freshness_SetsValue()
    {
        var config = RankingBuilder.Create().Freshness("1709596800").Build();
        Assert.Equal("1709596800", config.Freshness);
    }

    [Fact]
    public void QueryCache_SetsTrue()
    {
        var config = RankingBuilder.Create().QueryCache().Build();
        Assert.True(config.QueryCache);
    }

    [Fact]
    public void KeepRankCount_SetsValue()
    {
        var config = RankingBuilder.Create().KeepRankCount(500).Build();
        Assert.Equal(500, config.KeepRankCount);
    }

    [Fact]
    public void RankScoreDropLimit_SetsValue()
    {
        var config = RankingBuilder.Create().RankScoreDropLimit(0.001).Build();
        Assert.Equal(0.001, config.RankScoreDropLimit);
    }

    [Fact]
    public void GlobalPhaseRerankCount_SetsValue()
    {
        var config = RankingBuilder.Create().GlobalPhaseRerankCount(1000).Build();
        Assert.Equal(1000, config.GlobalPhaseRerankCount);
    }

    [Fact]
    public void MatchPhase_SetsAllFields()
    {
        var config = RankingBuilder.Create()
            .MatchPhase("timestamp", maxHits: 10000, ascending: true)
            .Build();
        Assert.Equal("timestamp", config.MatchPhaseAttribute);
        Assert.Equal(10000, config.MatchPhaseMaxHits);
        Assert.True(config.MatchPhaseAscending);
    }

    [Fact]
    public void MatchPhaseDiversity_SetsFields()
    {
        var config = RankingBuilder.Create()
            .MatchPhaseDiversity("category", minGroups: 5)
            .Build();
        Assert.Equal("category", config.MatchPhaseDiversityAttribute);
        Assert.Equal(5, config.MatchPhaseDiversityMinGroups);
    }

    [Fact]
    public void FullPipeline_WithAllNewFields()
    {
        var config = RankingBuilder.WithProfile("semantic")
            .Feature("query(threshold)", 0.7)
            .RerankCount(200)
            .Sorting("+year")
            .SoftTimeout(true, 0.5)
            .Freshness("1709596800")
            .QueryCache()
            .KeepRankCount(300)
            .RankScoreDropLimit(0.01)
            .GlobalPhaseRerankCount(500)
            .MatchPhase("timestamp", maxHits: 5000, ascending: false)
            .MatchPhaseDiversity("genre", minGroups: 3)
            .Build();

        Assert.Equal("semantic", config.Profile);
        Assert.Equal(200, config.RerankCount);
        Assert.Equal("+year", config.Sorting);
        Assert.True(config.SoftTimeoutEnable);
        Assert.Equal(0.5, config.SoftTimeoutFactor);
        Assert.Equal("1709596800", config.Freshness);
        Assert.True(config.QueryCache);
        Assert.Equal(300, config.KeepRankCount);
        Assert.Equal(0.01, config.RankScoreDropLimit);
        Assert.Equal(500, config.GlobalPhaseRerankCount);
        Assert.Equal("timestamp", config.MatchPhaseAttribute);
        Assert.Equal(5000, config.MatchPhaseMaxHits);
        Assert.False(config.MatchPhaseAscending);
        Assert.Equal("genre", config.MatchPhaseDiversityAttribute);
        Assert.Equal(3, config.MatchPhaseDiversityMinGroups);
    }

    // --- M13: Matching parameters ---

    [Fact]
    public void Matching_SetsAllFields()
    {
        var config = RankingBuilder.WithProfile("p")
            .Matching(numThreadsPerSearch: 4, minHitsPerThread: 100, termwiseLimit: 0.01, approximateThreshold: 0.05)
            .Build();

        Assert.Equal(4, config.MatchingNumThreadsPerSearch);
        Assert.Equal(100, config.MatchingMinHitsPerThread);
        Assert.Equal(0.01, config.MatchingTermwiseLimit);
        Assert.Equal(0.05, config.MatchingApproximateThreshold);
    }

    [Fact]
    public void Matching_PartialFields()
    {
        var config = RankingBuilder.WithProfile("p")
            .Matching(numThreadsPerSearch: 2)
            .Build();

        Assert.Equal(2, config.MatchingNumThreadsPerSearch);
        Assert.Null(config.MatchingMinHitsPerThread);
        Assert.Null(config.MatchingTermwiseLimit);
    }

    [Fact]
    public void SignificanceUseModel_SetsTrue()
    {
        var config = RankingBuilder.WithProfile("p").SignificanceUseModel().Build();
        Assert.True(config.SignificanceUseModel);
    }

    [Fact]
    public void SignificanceUseModel_NotCalled_IsNull()
    {
        var config = RankingBuilder.WithProfile("p").Build();
        Assert.Null(config.SignificanceUseModel);
    }
}
