using System.Text.Json;
using Vespa.Models;
using Xunit;

namespace Vespa.Tests.Unit;

public class SearchRequestSerializationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // --- Model parameters ---

    [Fact]
    public void ModelRestrict_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", ModelRestrict = "music,video" };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"model.restrict\":\"music,video\"", json);
    }

    [Fact]
    public void ModelSources_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", ModelSources = "cluster1,cluster2" };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"model.sources\":\"cluster1,cluster2\"", json);
    }

    [Fact]
    public void ModelType_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", ModelType = "weakAnd" };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"model.type\":\"weakAnd\"", json);
    }

    [Fact]
    public void ModelDefaultIndex_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", ModelDefaultIndex = "title" };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"model.defaultIndex\":\"title\"", json);
    }

    [Fact]
    public void ModelFilter_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", ModelFilter = "year:2000" };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"model.filter\":\"year:2000\"", json);
    }

    [Fact]
    public void ModelLocale_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", ModelLocale = "en-US" };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"model.locale\":\"en-US\"", json);
    }

    [Fact]
    public void ModelQueryString_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", ModelQueryString = "rock and roll" };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"model.queryString\":\"rock and roll\"", json);
    }

    [Fact]
    public void ModelSearchPath_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", ModelSearchPath = "0/0" };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"model.searchPath\":\"0/0\"", json);
    }

    // --- Trace / diagnostics ---

    [Fact]
    public void TraceExplainLevel_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", TraceExplainLevel = 2 };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"trace.explainLevel\":2", json);
    }

    [Fact]
    public void TraceProfileDepth_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", TraceProfileDepth = 3 };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"trace.profileDepth\":3", json);
    }

    [Fact]
    public void TraceTimestamps_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", TraceTimestamps = true };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"trace.timestamps\":true", json);
    }

    // --- Presentation extras ---

    [Fact]
    public void PresentationTiming_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", PresentationTiming = true };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"presentation.timing\":true", json);
    }

    [Fact]
    public void PresentationFormatTensors_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", PresentationFormatTensors = "short" };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"presentation.format.tensors\":\"short\"", json);
    }

    // --- Grouping tuning ---

    [Fact]
    public void GroupingDefaultMaxGroups_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", GroupingDefaultMaxGroups = 50 };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"grouping.defaultMaxGroups\":50", json);
    }

    [Fact]
    public void GroupingDefaultMaxHits_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", GroupingDefaultMaxHits = 20 };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"grouping.defaultMaxHits\":20", json);
    }

    [Fact]
    public void GroupingGlobalMaxGroups_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", GroupingGlobalMaxGroups = 5000 };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"grouping.globalMaxGroups\":5000", json);
    }

    // --- Other ---

    [Fact]
    public void SearchChain_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", SearchChain = "mychain" };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"searchChain\":\"mychain\"", json);
    }

    [Fact]
    public void Recall_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", Recall = "title:rock" };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"recall\":\"title:rock\"", json);
    }

    [Fact]
    public void NoCache_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", NoCache = true };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"noCache\":true", json);
    }

    [Fact]
    public void HitCountEstimate_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", HitCountEstimate = true };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"hitcountestimate\":true", json);
    }

    // --- Null fields not emitted ---

    [Fact]
    public void NullFields_NotEmitted()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;" };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.DoesNotContain("model.restrict", json);
        Assert.DoesNotContain("model.sources", json);
        Assert.DoesNotContain("searchChain", json);
        Assert.DoesNotContain("recall", json);
        Assert.DoesNotContain("noCache", json);
        Assert.DoesNotContain("trace.explainLevel", json);
        Assert.DoesNotContain("presentation.timing", json);
        Assert.DoesNotContain("grouping.defaultMaxGroups", json);
    }

    // --- RankingConfig new fields ---

    [Fact]
    public void RankingConfig_SoftTimeout_Serializes()
    {
        var config = new RankingConfig { SoftTimeoutEnable = true, SoftTimeoutFactor = 0.7 };
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains("\"softtimeout.enable\":true", json);
        Assert.Contains("\"softtimeout.factor\":0.7", json);
    }

    [Fact]
    public void RankingConfig_Freshness_Serializes()
    {
        var config = new RankingConfig { Freshness = "1709596800" };
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains("\"freshness\":\"1709596800\"", json);
    }

    [Fact]
    public void RankingConfig_QueryCache_Serializes()
    {
        var config = new RankingConfig { QueryCache = true };
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains("\"queryCache\":true", json);
    }

    [Fact]
    public void RankingConfig_KeepRankCount_Serializes()
    {
        var config = new RankingConfig { KeepRankCount = 500 };
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains("\"keepRankCount\":500", json);
    }

    [Fact]
    public void RankingConfig_RankScoreDropLimit_Serializes()
    {
        var config = new RankingConfig { RankScoreDropLimit = 0.001 };
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains("\"rankScoreDropLimit\":0.001", json);
    }

    [Fact]
    public void RankingConfig_GlobalPhaseRerankCount_Serializes()
    {
        var config = new RankingConfig { GlobalPhaseRerankCount = 1000 };
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains("\"globalPhase.rerankCount\":1000", json);
    }

    [Fact]
    public void RankingConfig_MatchPhase_Serializes()
    {
        var config = new RankingConfig
        {
            MatchPhaseAttribute = "timestamp",
            MatchPhaseMaxHits = 10000,
            MatchPhaseAscending = true,
            MatchPhaseDiversityAttribute = "category",
            MatchPhaseDiversityMinGroups = 5
        };
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains("\"matchPhase.attribute\":\"timestamp\"", json);
        Assert.Contains("\"matchPhase.maxHits\":10000", json);
        Assert.Contains("\"matchPhase.ascending\":true", json);
        Assert.Contains("\"matchPhase.diversity.attribute\":\"category\"", json);
        Assert.Contains("\"matchPhase.diversity.minGroups\":5", json);
    }

    [Fact]
    public void RankingConfig_Sorting_Serializes()
    {
        var config = new RankingConfig { Sorting = "year -relevance" };
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains("\"sorting\":\"year -relevance\"", json);
    }

    [Fact]
    public void RankingConfig_NullNewFields_NotEmitted()
    {
        var config = new RankingConfig { Profile = "default" };
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.DoesNotContain("softtimeout", json);
        Assert.DoesNotContain("freshness", json);
        Assert.DoesNotContain("queryCache", json);
        Assert.DoesNotContain("keepRankCount", json);
        Assert.DoesNotContain("rankScoreDropLimit", json);
        Assert.DoesNotContain("matchPhase", json);
        Assert.DoesNotContain("sorting", json);
    }

    // --- M13: New search request parameters ---

    [Fact]
    public void GroupingSessionCache_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", GroupingSessionCache = true };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"groupingSessionCache\":true", json);
    }

    [Fact]
    public void DispatchTopKProbability_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", DispatchTopKProbability = 0.99 };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"dispatch.topKProbability\":0.99", json);
    }

    [Fact]
    public void WeakAndReplace_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", WeakAndReplace = true };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"weakAnd.replace\":true", json);
    }

    [Fact]
    public void WandHits_Serializes()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;", WandHits = 500 };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"wand.hits\":500", json);
    }

    // --- M13: Ranking matching parameters ---

    [Fact]
    public void RankingConfig_MatchingNumThreadsPerSearch_Serializes()
    {
        var config = new RankingConfig { MatchingNumThreadsPerSearch = 4 };
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains("\"matching.numThreadsPerSearch\":4", json);
    }

    [Fact]
    public void RankingConfig_MatchingMinHitsPerThread_Serializes()
    {
        var config = new RankingConfig { MatchingMinHitsPerThread = 100 };
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains("\"matching.minHitsPerThread\":100", json);
    }

    [Fact]
    public void RankingConfig_MatchingTermwiseLimit_Serializes()
    {
        var config = new RankingConfig { MatchingTermwiseLimit = 0.01 };
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains("\"matching.termwiseLimit\":0.01", json);
    }

    [Fact]
    public void RankingConfig_MatchingApproximateThreshold_Serializes()
    {
        var config = new RankingConfig { MatchingApproximateThreshold = 0.05 };
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains("\"matching.approximateThreshold\":0.05", json);
    }

    [Fact]
    public void RankingConfig_SignificanceUseModel_Serializes()
    {
        var config = new RankingConfig { SignificanceUseModel = true };
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.Contains("\"significance.useModel\":true", json);
    }

    [Fact]
    public void RankingConfig_NullMatchingFields_NotEmitted()
    {
        var config = new RankingConfig { Profile = "default" };
        var json = JsonSerializer.Serialize(config, JsonOpts);
        Assert.DoesNotContain("matching.", json);
        Assert.DoesNotContain("significance.", json);
    }
}
