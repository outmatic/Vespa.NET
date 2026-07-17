using System.Text.Json;
using Vespa.Models;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for response DTO behavior: full document ID retention on
/// <see cref="VespaDocument{T}"/>/<see cref="SearchHit{T}"/>, coverage degradation,
/// summary features, typed group hits, and metric value aggregations.
/// </summary>
public class ResponseModelTests
{
    private sealed record TestFields
    {
        public string? Name { get; init; }
    }

    // ── Full ID retention (VespaDocument) ────────────────────────────────────

    [Fact]
    public void VespaDocument_Deserialization_RetainsFullId_AndDerivesShortId()
    {
        var json = """{"id":"id:ns:music::doc1","fields":{"Name":"x"}}""";

        var doc = JsonSerializer.Deserialize<VespaDocument<TestFields>>(json);

        Assert.NotNull(doc);
        Assert.Equal("id:ns:music::doc1", doc.FullId);
        Assert.Equal("doc1", doc.Id);
    }

    [Theory]
    [InlineData("id:namespace:documenttype::doc1", "doc1")]
    [InlineData("id:test:testdoc::12345", "12345")]
    [InlineData("id:ns:type::my:id:with:colons", "my:id:with:colons")]
    // User-specified part may itself contain "::" (id grammar: id:<ns>:<type>:<k/v>:<user>)
    [InlineData("id:ns:music::album::dark-side", "album::dark-side")]
    // Location selectors (g=/n=) occupy the key/value slot; user part keeps its colons
    [InlineData("id:ns:music:g=group1:doc:1", "doc:1")]
    [InlineData("id:ns:music:n=123:42", "42")]
    // Strings that are not full document IDs pass through unchanged
    [InlineData("just-an-id", "just-an-id")]
    [InlineData("a::b", "a::b")]
    [InlineData("", "")]
    public void VespaDocument_ShortId_DerivedFromFullId(string fullId, string expectedShortId)
    {
        var doc = new VespaDocument<TestFields> { FullId = fullId };

        Assert.Equal(expectedShortId, doc.Id);
        Assert.Equal(fullId, doc.FullId);
    }

    [Fact]
    public void VespaDocument_ExplicitId_OverridesDerivedShortId()
    {
        var doc = new VespaDocument<TestFields> { FullId = "id:ns:music::doc1", Id = "custom" };

        Assert.Equal("custom", doc.Id);
        Assert.Equal("id:ns:music::doc1", doc.FullId);
    }

    // ── Full ID retention (SearchHit) ────────────────────────────────────────

    [Fact]
    public void SearchHit_Deserialization_RetainsFullId_AndDerivesShortId()
    {
        var json = """{"id":"id:ns:music::doc1","relevance":0.5,"fields":{"Name":"x"}}""";

        var hit = JsonSerializer.Deserialize<SearchHit<TestFields>>(json);

        Assert.NotNull(hit);
        Assert.Equal("id:ns:music::doc1", hit.FullId);
        Assert.Equal("doc1", hit.Id);
        Assert.Equal(0.5, hit.Relevance);
    }

    [Theory]
    // Non-document hit IDs (grouping results, index hits) must never be shortened
    [InlineData("group:root:0")]
    [InlineData("index:content/0/abc123")]
    public void SearchHit_NonDocumentIds_PassThroughUnchanged(string hitId)
    {
        var hit = JsonSerializer.Deserialize<SearchHit<TestFields>>($$"""{"id":"{{hitId}}"}""");

        Assert.NotNull(hit);
        Assert.Equal(hitId, hit.FullId);
        Assert.Equal(hitId, hit.Id);
    }

    // ── Coverage degradation ─────────────────────────────────────────────────

    [Fact]
    public void Coverage_Degraded_Deserializes()
    {
        var json = """
            {
                "coverage": 83,
                "documents": 275,
                "degraded": {
                    "match-phase": false,
                    "timeout": true,
                    "adaptive-timeout": false,
                    "non-ideal-state": false
                },
                "full": false,
                "nodes": 1,
                "results": 1,
                "resultsFull": 0
            }
            """;

        var coverage = JsonSerializer.Deserialize<Coverage>(json);

        Assert.NotNull(coverage);
        Assert.Equal(83, coverage.CoveragePercentage);
        Assert.NotNull(coverage.Degraded);
        Assert.True(coverage.Degraded.Timeout);
        Assert.False(coverage.Degraded.MatchPhase);
        Assert.False(coverage.Degraded.AdaptiveTimeout);
        Assert.False(coverage.Degraded.AnnTimeout);
        Assert.False(coverage.Degraded.NonIdealState);
    }

    [Fact]
    public void Coverage_WithoutDegraded_IsNull()
    {
        var coverage = JsonSerializer.Deserialize<Coverage>("""{"coverage":100,"full":true}""");

        Assert.NotNull(coverage);
        Assert.Null(coverage.Degraded);
    }

    // ── Match/summary features ───────────────────────────────────────────────

    private sealed record FeatureFields
    {
        [System.Text.Json.Serialization.JsonPropertyName("title")]
        public string? Title { get; init; }
    }

    // Mirrors consumer workarounds that declare the pseudo-field on T directly;
    // extracting features at hit level must not break them.
    private sealed record WorkaroundFields
    {
        [System.Text.Json.Serialization.JsonPropertyName("title")]
        public string? Title { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("matchfeatures")]
        public Dictionary<string, JsonElement>? MatchFeatures { get; init; }
    }

    [Fact]
    public void SearchHit_MatchFeatures_InsideFields_DefaultRenderer_ArePopulated()
    {
        // Vespa's default JSON renderer emits matchfeatures as a pseudo-field
        // INSIDE "fields", not at hit level.
        var json = """
            {
                "id": "index:rag/0/abc",
                "relevance": 0.5,
                "source": "rag",
                "fields": {
                    "sddocname": "doc",
                    "documentid": "id:rag:doc::1",
                    "title": "hello",
                    "matchfeatures": {
                        "bm25(title)": 1.5,
                        "bm25(chunks)": 2.5,
                        "max_chunk_sim_scores": 0.9
                    }
                }
            }
            """;

        var hit = JsonSerializer.Deserialize<SearchHit<FeatureFields>>(json);

        Assert.NotNull(hit);
        Assert.NotNull(hit.MatchFeatures);
        Assert.Equal(1.5, hit.GetMatchFeature("bm25(title)"));
        Assert.Equal(2.5, hit.GetMatchFeature("bm25(chunks)"));
        Assert.Equal(0.9, hit.GetMatchFeature("max_chunk_sim_scores"));
        Assert.Equal("hello", hit.Fields?.Title);
        Assert.Equal("index:rag/0/abc", hit.FullId);
        Assert.Equal(0.5, hit.Relevance);
        Assert.Equal("rag", hit.Source);
    }

    [Fact]
    public void SearchHit_SummaryFeatures_InsideFields_ArePopulated()
    {
        var json = """
            {
                "id": "id:ns:music::doc1",
                "relevance": 1.0,
                "fields": {
                    "title": "hello",
                    "summaryfeatures": { "attribute(year)": 2001.0 }
                }
            }
            """;

        var hit = JsonSerializer.Deserialize<SearchHit<FeatureFields>>(json);

        Assert.NotNull(hit);
        Assert.Equal(2001.0, hit.GetSummaryFeature("attribute(year)"));
        Assert.Equal("hello", hit.Fields?.Title);
    }

    [Fact]
    public void SearchHit_Features_FieldsLevel_WinsOverHitLevel()
    {
        var json = """
            {
                "id": "id:ns:music::doc1",
                "relevance": 1.0,
                "matchfeatures": { "bm25(title)": 99.0 },
                "fields": {
                    "matchfeatures": { "bm25(title)": 1.5 }
                }
            }
            """;

        var hit = JsonSerializer.Deserialize<SearchHit<FeatureFields>>(json);

        Assert.NotNull(hit);
        Assert.Equal(1.5, hit.GetMatchFeature("bm25(title)"));
    }

    [Fact]
    public void SearchHit_MatchFeatures_AtHitLevel_StillRead()
    {
        // Custom renderers may put the features at hit level; keep reading them there.
        var json = """
            {
                "id": "id:ns:music::doc1",
                "relevance": 1.0,
                "matchfeatures": { "bm25(title)": 1.5 }
            }
            """;

        var hit = JsonSerializer.Deserialize<SearchHit<FeatureFields>>(json);

        Assert.NotNull(hit);
        Assert.Equal(1.5, hit.GetMatchFeature("bm25(title)"));
    }

    [Fact]
    public void SearchHit_FieldsTypeDeclaringMatchfeatures_StillReceivesThem()
    {
        var json = """
            {
                "id": "id:ns:music::doc1",
                "relevance": 1.0,
                "fields": {
                    "title": "hello",
                    "matchfeatures": { "bm25(title)": 1.5 }
                }
            }
            """;

        var hit = JsonSerializer.Deserialize<SearchHit<WorkaroundFields>>(json);

        Assert.NotNull(hit);
        // Hit-level accessor works…
        Assert.Equal(1.5, hit.GetMatchFeature("bm25(title)"));
        // …and the consumer's own property keeps receiving the pseudo-field.
        Assert.NotNull(hit.Fields?.MatchFeatures);
        Assert.Equal(1.5, hit.Fields.MatchFeatures["bm25(title)"].GetDouble());
    }

    [Fact]
    public void SearchHit_SerializeDeserialize_RoundTripsFeatures()
    {
        var original = new SearchHit<FeatureFields>
        {
            FullId = "id:ns:music::doc1",
            Relevance = 0.7,
            Fields = new FeatureFields { Title = "hello" },
            MatchFeatures = new Dictionary<string, JsonElement>
            {
                ["bm25(title)"] = JsonDocument.Parse("1.5").RootElement
            }
        };

        var roundTripped = JsonSerializer.Deserialize<SearchHit<FeatureFields>>(
            JsonSerializer.Serialize(original));

        Assert.NotNull(roundTripped);
        Assert.Equal("id:ns:music::doc1", roundTripped.FullId);
        Assert.Equal(0.7, roundTripped.Relevance);
        Assert.Equal("hello", roundTripped.Fields?.Title);
        Assert.Equal(1.5, roundTripped.GetMatchFeature("bm25(title)"));
    }

    [Fact]
    public void SearchHit_SummaryFeatures_DeserializeAndExposeScalars()
    {
        var json = """
            {
                "id": "id:ns:music::doc1",
                "relevance": 1.0,
                "summaryfeatures": {
                    "attribute(year)": 2001.0,
                    "tensorFeature": { "type": "tensor(x[2])", "values": [1.0, 2.0] }
                }
            }
            """;

        var hit = JsonSerializer.Deserialize<SearchHit<TestFields>>(json);

        Assert.NotNull(hit);
        Assert.NotNull(hit.SummaryFeatures);
        Assert.Equal(2001.0, hit.GetSummaryFeature("attribute(year)"));
        Assert.Null(hit.GetSummaryFeature("tensorFeature")); // not a scalar
        Assert.Null(hit.GetSummaryFeature("missing"));
    }

    // ── Typed group hits ─────────────────────────────────────────────────────

    [Fact]
    public void VespaGroup_GetHits_ReturnsTypedHits()
    {
        var hit = new SearchHit<TestFields> { FullId = "id:ns:music::doc1", Fields = new TestFields { Name = "x" } };
        var group = new VespaGroup("rock", new Dictionary<string, double>(), []) { Hits = [hit] };

        var typed = group.GetHits<TestFields>();

        Assert.Single(typed);
        Assert.Equal("doc1", typed[0].Id);
        Assert.Equal("x", typed[0].Fields!.Name);
    }

    [Fact]
    public void VespaGroup_GetHits_WrongType_ThrowsInvalidCast()
    {
        var hit = new SearchHit<TestFields> { FullId = "id:ns:music::doc1" };
        var group = new VespaGroup("rock", new Dictionary<string, double>(), []) { Hits = [hit] };

        Assert.Throws<InvalidCastException>(() => group.GetHits<VespaError>());
    }

    // ── Metric value aggregations ────────────────────────────────────────────

    [Fact]
    public void MetricValues_ExtendedAggregations_Deserialize()
    {
        var json = """
            {
                "count": 300,
                "last": 1.5,
                "max": 42.0,
                "min": 0.1,
                "sum": 450.0,
                "average": 1.5,
                "rate": 5.0,
                "95percentile": 3.2,
                "99percentile": 4.8
            }
            """;

        var values = JsonSerializer.Deserialize<MetricValues>(json);

        Assert.NotNull(values);
        Assert.Equal(300, values.Count);
        Assert.Equal(0.1, values.Min);
        Assert.Equal(1.5, values.Average);
        Assert.Equal(5.0, values.Rate);
        Assert.Equal(3.2, values.Percentile95);
        Assert.Equal(4.8, values.Percentile99);
    }
}
