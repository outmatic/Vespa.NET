using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Represents a Vespa search request
/// </summary>
public sealed class VespaSearchRequest
{
    /// <summary>
    /// YQL query string
    /// </summary>
    [JsonPropertyName("yql")]
    public string Yql { get; set; } = string.Empty;

    /// <summary>
    /// Number of hits to return
    /// </summary>
    [JsonPropertyName("hits")]
    public int Hits { get; set; } = 10;

    /// <summary>
    /// Offset for pagination
    /// </summary>
    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    /// <summary>
    /// Ranking configuration
    /// </summary>
    [JsonPropertyName("ranking")]
    public RankingConfig? Ranking { get; set; }

    /// <summary>
    /// Input parameters (e.g., embeddings, query parameters)
    /// </summary>
    [JsonPropertyName("input")]
    public Dictionary<string, object>? Input { get; set; }

    /// <summary>
    /// Query timeout
    /// </summary>
    [JsonPropertyName("timeout")]
    public string? Timeout { get; set; }

    /// <summary>
    /// Trace level for debugging
    /// </summary>
    [JsonPropertyName("trace.level")]
    public int? TraceLevel { get; set; }

    // ── Vespa streaming search mode ────────────────────────────────────────────

    /// <summary>
    /// Activate Vespa streaming search for this user ID.
    /// Requires a content cluster configured with <c>streaming</c> mode.
    /// </summary>
    [JsonPropertyName("streaming.userid")]
    public string? StreamingUserId { get; set; }

    /// <summary>
    /// Activate Vespa streaming search for this group name.
    /// </summary>
    [JsonPropertyName("streaming.groupname")]
    public string? StreamingGroupName { get; set; }

    /// <summary>
    /// Additional document selection expression for streaming search
    /// (applied on top of the YQL where clause).
    /// </summary>
    [JsonPropertyName("streaming.selection")]
    public string? StreamingSelection { get; set; }

    /// <summary>
    /// Maximum number of distribution buckets to visit per search call.
    /// Tune for latency vs. coverage trade-off in streaming mode.
    /// </summary>
    [JsonPropertyName("streaming.maxbucketspervisitor")]
    public int? StreamingMaxBucketsPerVisit { get; set; }

    // ── Collapse / Deduplication ───────────────────────────────────────────────

    /// <summary>
    /// Deduplicate results by this field — only the highest-ranked hit per unique
    /// field value is returned. Equivalent to Vespa's <c>collapsefield</c> parameter.
    /// </summary>
    [JsonPropertyName("collapsefield")]
    public string? CollapseField { get; set; }

    /// <summary>
    /// Maximum number of hits to return per collapsed group (default 1).
    /// Equivalent to Vespa's <c>collapsesize</c> parameter.
    /// </summary>
    [JsonPropertyName("collapsesize")]
    public int? CollapseSize { get; set; }

    // ── Presentation / Highlighting ────────────────────────────────────────────

    /// <summary>
    /// Enable bold-tag highlighting of matching query terms in string fields.
    /// Equivalent to Vespa's <c>presentation.bolding</c> parameter.
    /// </summary>
    [JsonPropertyName("presentation.bolding")]
    public bool? PresentationBolding { get; set; }

    /// <summary>
    /// Override the response presentation format.
    /// Equivalent to Vespa's <c>presentation.format</c> parameter (e.g. <c>"json"</c>).
    /// </summary>
    [JsonPropertyName("presentation.format")]
    public string? PresentationFormat { get; set; }

    /// <summary>
    /// Summary class to use for rendering hits (controls which fields are returned).
    /// Equivalent to Vespa's <c>presentation.summary</c> parameter.
    /// </summary>
    [JsonPropertyName("presentation.summary")]
    public string? PresentationSummary { get; set; }

    // ── Query Profile ──────────────────────────────────────────────────────────

    /// <summary>
    /// Named query profile to apply (overrides default query parameters with profile values).
    /// Equivalent to Vespa's <c>queryProfile</c> parameter.
    /// </summary>
    [JsonPropertyName("queryProfile")]
    public string? QueryProfile { get; set; }

    // ── Model parameters ─────────────────────────────────────────────────

    /// <summary>Limit search to specific document types (comma-separated).</summary>
    [JsonPropertyName("model.restrict")]
    public string? ModelRestrict { get; set; }

    /// <summary>Comma-separated content cluster or federated source names.</summary>
    [JsonPropertyName("model.sources")]
    public string? ModelSources { get; set; }

    /// <summary>Composite query type: weakAnd, and, or, phrase, etc.</summary>
    [JsonPropertyName("model.type")]
    public string? ModelType { get; set; }

    /// <summary>Field searched when index not specified.</summary>
    [JsonPropertyName("model.defaultIndex")]
    public string? ModelDefaultIndex { get; set; }

    /// <summary>Additional filter combined with the query.</summary>
    [JsonPropertyName("model.filter")]
    public string? ModelFilter { get; set; }

    /// <summary>RFC 5646 language tag for query parsing.</summary>
    [JsonPropertyName("model.locale")]
    public string? ModelLocale { get; set; }

    /// <summary>Simple query language query text.</summary>
    [JsonPropertyName("model.queryString")]
    public string? ModelQueryString { get; set; }

    /// <summary>Target specific content nodes (advanced).</summary>
    [JsonPropertyName("model.searchPath")]
    public string? ModelSearchPath { get; set; }

    // ── Trace / diagnostics ───────────────────────────────────────────────

    /// <summary>Content node explanation level (1-2).</summary>
    [JsonPropertyName("trace.explainLevel")]
    public int? TraceExplainLevel { get; set; }

    /// <summary>Performance profiling depth.</summary>
    [JsonPropertyName("trace.profileDepth")]
    public int? TraceProfileDepth { get; set; }

    /// <summary>Enable timing at trace level 1.</summary>
    [JsonPropertyName("trace.timestamps")]
    public bool? TraceTimestamps { get; set; }

    // ── Presentation extras ───────────────────────────────────────────────

    /// <summary>Include optional timing information in response.</summary>
    [JsonPropertyName("presentation.timing")]
    public bool? PresentationTiming { get; set; }

    /// <summary>Tensor rendering format: short, long, short-value, long-value, hex.</summary>
    [JsonPropertyName("presentation.format.tensors")]
    public string? PresentationFormatTensors { get; set; }

    // ── Grouping tuning ───────────────────────────────────────────────────

    /// <summary>Default group limit per level (default 10).</summary>
    [JsonPropertyName("grouping.defaultMaxGroups")]
    public int? GroupingDefaultMaxGroups { get; set; }

    /// <summary>Default hits per group (default 10).</summary>
    [JsonPropertyName("grouping.defaultMaxHits")]
    public int? GroupingDefaultMaxHits { get; set; }

    /// <summary>Global cost limit for grouping (default 10000).</summary>
    [JsonPropertyName("grouping.globalMaxGroups")]
    public int? GroupingGlobalMaxGroups { get; set; }

    // ── Other ─────────────────────────────────────────────────────────────

    /// <summary>Custom search chain to invoke.</summary>
    [JsonPropertyName("searchChain")]
    public string? SearchChain { get; set; }

    /// <summary>Non-ranked filter constraints.</summary>
    [JsonPropertyName("recall")]
    public string? Recall { get; set; }

    /// <summary>Never cache this query.</summary>
    [JsonPropertyName("noCache")]
    public bool? NoCache { get; set; }

    /// <summary>Return only hit count, no documents.</summary>
    [JsonPropertyName("hitcountestimate")]
    public bool? HitCountEstimate { get; set; }

    /// <summary>Enable grouping session cache for continuation queries.</summary>
    [JsonPropertyName("groupingSessionCache")]
    public bool? GroupingSessionCache { get; set; }

    /// <summary>Probability threshold for top-K dispatch optimization.</summary>
    [JsonPropertyName("dispatch.topKProbability")]
    public double? DispatchTopKProbability { get; set; }

    /// <summary>Replace weakAnd with AND semantics.</summary>
    [JsonPropertyName("weakAnd.replace")]
    public bool? WeakAndReplace { get; set; }

    /// <summary>Target hits for wand operator.</summary>
    [JsonPropertyName("wand.hits")]
    public int? WandHits { get; set; }

    // ── Custom / dynamic parameters ─────────────────────────────────────────────

    /// <summary>
    /// Custom request parameters serialized as top-level JSON keys.
    /// Use for <c>@paramName</c> references in YQL (e.g. <c>userInput(@text)</c>)
    /// or any other arbitrary Vespa request parameter.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? CustomParameters { get; set; }

    // ── Cloning ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a shallow copy of this request suitable for pagination.
    /// Cheaper than a JSON serialize→deserialize round-trip.
    /// </summary>
    public VespaSearchRequest ShallowClone() => (VespaSearchRequest)MemberwiseClone();

    // ── Fluent helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the per-request timeout from a <see cref="TimeSpan"/>.
    /// Vespa format: milliseconds rounded to the nearest ms (e.g. <c>"500ms"</c>, <c>"5000ms"</c>).
    /// </summary>
    public VespaSearchRequest WithTimeout(TimeSpan timeout)
    {
        Timeout = $"{(long)timeout.TotalMilliseconds}ms";
        return this;
    }

    /// <summary>
    /// Deduplicates results by <paramref name="field"/>, keeping the top
    /// <paramref name="size"/> hits per unique value.
    /// </summary>
    public VespaSearchRequest WithCollapse(string field, int size = 1)
    {
        CollapseField = field;
        CollapseSize = size;
        return this;
    }

    /// <summary>
    /// Adds an arbitrary top-level parameter to the search request JSON body.
    /// Useful for <c>input.query(...)</c>, <c>ranking.features.query(...)</c>,
    /// or any other Vespa parameter not covered by typed properties.
    /// </summary>
    public VespaSearchRequest WithParameter(string key, object value)
    {
        CustomParameters ??= [];
        CustomParameters[key] = value;
        return this;
    }
}

/// <summary>
/// Ranking configuration for search
/// </summary>
public sealed class RankingConfig
{
    /// <summary>Ranking profile name</summary>
    [JsonPropertyName("profile")]
    public string? Profile { get; set; }

    /// <summary>
    /// Explicit rank feature overrides sent to the ranking expression.
    /// Keys are feature names (e.g. <c>"query(threshold)"</c>); values are scalars or tensor objects.
    /// </summary>
    [JsonPropertyName("features")]
    public Dictionary<string, object>? Features { get; set; }

    /// <summary>
    /// Additional ranking properties (overrides for rank-property values in the profile)
    /// </summary>
    [JsonPropertyName("properties")]
    public Dictionary<string, object>? Properties { get; set; }

    /// <summary>
    /// Number of top-N documents to consider for second-phase re-ranking
    /// </summary>
    [JsonPropertyName("rerankCount")]
    public int? RerankCount { get; set; }

    /// <summary>Return all computed rank features per hit (useful for debugging)</summary>
    [JsonPropertyName("listFeatures")]
    public bool? ListFeatures { get; set; }

    /// <summary>Sort specification string (e.g. "+year -relevance").</summary>
    [JsonPropertyName("sorting")]
    public string? Sorting { get; set; }

    /// <summary>Enable soft timeout — return partial results when time runs out.</summary>
    [JsonPropertyName("softtimeout.enable")]
    public bool? SoftTimeoutEnable { get; set; }

    /// <summary>Fraction of timeout budget for first phase (0.0-1.0, default 0.7).</summary>
    [JsonPropertyName("softtimeout.factor")]
    public double? SoftTimeoutFactor { get; set; }

    /// <summary>Time reference for freshness-based ranking (epoch seconds).</summary>
    [JsonPropertyName("freshness")]
    public string? Freshness { get; set; }

    /// <summary>Cache the query between ranking phases.</summary>
    [JsonPropertyName("queryCache")]
    public bool? QueryCache { get; set; }

    /// <summary>Documents keeping rank value after match phase.</summary>
    [JsonPropertyName("keepRankCount")]
    public int? KeepRankCount { get; set; }

    /// <summary>Minimum first-phase score threshold. Documents scoring below are dropped.</summary>
    [JsonPropertyName("rankScoreDropLimit")]
    public double? RankScoreDropLimit { get; set; }

    /// <summary>Global phase rerank count.</summary>
    [JsonPropertyName("globalPhase.rerankCount")]
    public int? GlobalPhaseRerankCount { get; set; }

    // ── Match phase (early termination) ───────────────────────────────────

    /// <summary>Attribute for match-phase limiting.</summary>
    [JsonPropertyName("matchPhase.attribute")]
    public string? MatchPhaseAttribute { get; set; }

    /// <summary>Maximum hits during match phase.</summary>
    [JsonPropertyName("matchPhase.maxHits")]
    public long? MatchPhaseMaxHits { get; set; }

    /// <summary>Keep lowest values (true) or highest (false, default).</summary>
    [JsonPropertyName("matchPhase.ascending")]
    public bool? MatchPhaseAscending { get; set; }

    /// <summary>Diversity attribute for match-phase limiting.</summary>
    [JsonPropertyName("matchPhase.diversity.attribute")]
    public string? MatchPhaseDiversityAttribute { get; set; }

    /// <summary>Minimum diversity groups.</summary>
    [JsonPropertyName("matchPhase.diversity.minGroups")]
    public int? MatchPhaseDiversityMinGroups { get; set; }

    // ── Matching parameters ──────────────────────────────────────────────

    /// <summary>Number of threads per search on content nodes.</summary>
    [JsonPropertyName("matching.numThreadsPerSearch")]
    public int? MatchingNumThreadsPerSearch { get; set; }

    /// <summary>Minimum number of hits per thread before terminating.</summary>
    [JsonPropertyName("matching.minHitsPerThread")]
    public int? MatchingMinHitsPerThread { get; set; }

    /// <summary>Fraction of corpus below which term-wise evaluation kicks in (0.0-1.0).</summary>
    [JsonPropertyName("matching.termwiseLimit")]
    public double? MatchingTermwiseLimit { get; set; }

    /// <summary>Threshold for approximate matching (0.0-1.0).</summary>
    [JsonPropertyName("matching.approximateThreshold")]
    public double? MatchingApproximateThreshold { get; set; }

    // ── Significance ─────────────────────────────────────────────────────

    /// <summary>Use the language model for significance estimation.</summary>
    [JsonPropertyName("significance.useModel")]
    public bool? SignificanceUseModel { get; set; }
}
