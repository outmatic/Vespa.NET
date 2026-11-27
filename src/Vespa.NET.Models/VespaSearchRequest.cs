using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Represents a Vespa search request
/// </summary>
public sealed record VespaSearchRequest
{
    private readonly ImmutableDictionary<string, object?>? _input;

    /// <summary>
    /// YQL query string
    /// </summary>
    [JsonPropertyName("yql")]
    public string Yql { get; init; } = string.Empty;

    /// <summary>
    /// Number of hits to return
    /// </summary>
    [JsonPropertyName("hits")]
    public int Hits { get; init; } = 10;

    /// <summary>
    /// Offset for pagination
    /// </summary>
    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    /// <summary>
    /// Ranking configuration
    /// </summary>
    [JsonPropertyName("ranking")]
    public RankingConfig? Ranking { get; init; }

    /// <summary>
    /// Input parameters (e.g., embeddings, query parameters)
    /// </summary>
    [JsonPropertyName("input")]
    public IReadOnlyDictionary<string, object?>? Input
    {
        get => _input;
        init => _input = SearchParameterMap.Freeze(value);
    }

    /// <summary>
    /// Query timeout
    /// </summary>
    [JsonPropertyName("timeout")]
    public string? Timeout { get; init; }

    /// <summary>
    /// Trace level for debugging
    /// </summary>
    [JsonPropertyName("trace.level")]
    public int? TraceLevel { get; init; }

    // ── Vespa streaming search mode ────────────────────────────────────────────
    // streaming.userid is no longer part of the query API — use StreamingGroupName.

    /// <summary>
    /// Activate Vespa streaming search for this group name.
    /// Requires a content cluster configured with <c>streaming</c> mode.
    /// </summary>
    [JsonPropertyName("streaming.groupname")]
    public string? StreamingGroupName { get; init; }

    /// <summary>
    /// Document selector used instead of StreamingGroupName for streaming search.
    /// A disjunction of id.group equalities can restrict the groups visited.
    /// Other selections can scan all groups; YQL filters the selected documents separately.
    /// </summary>
    [JsonPropertyName("streaming.selection")]
    public string? StreamingSelection { get; init; }

    /// <summary>
    /// Maximum backend bucket concurrency for streaming search.
    /// This does not cap the total buckets visited or the result coverage.
    /// </summary>
    [JsonPropertyName("streaming.maxbucketspervisitor")]
    public int? StreamingMaxBucketsPerVisit { get; init; }

    // ── Collapse / Deduplication ───────────────────────────────────────────────

    /// <summary>
    /// Deduplicate results by this field — only the highest-ranked hit per unique
    /// field value is returned. Equivalent to Vespa's <c>collapsefield</c> parameter.
    /// </summary>
    [JsonPropertyName("collapsefield")]
    public string? CollapseField { get; init; }

    /// <summary>
    /// Maximum number of hits to return per collapsed group (default 1).
    /// Equivalent to Vespa's <c>collapsesize</c> parameter.
    /// </summary>
    [JsonPropertyName("collapsesize")]
    public int? CollapseSize { get; init; }

    /// <summary>
    /// Document summary class used to fetch the fields needed for collapsing.
    /// Equivalent to Vespa's <c>collapse.summary</c> parameter.
    /// </summary>
    [JsonPropertyName("collapse.summary")]
    public string? CollapseSummary { get; init; }

    // ── Presentation / Highlighting ────────────────────────────────────────────

    /// <summary>
    /// Enable bold-tag highlighting of matching query terms in string fields.
    /// Equivalent to Vespa's <c>presentation.bolding</c> parameter.
    /// </summary>
    [JsonPropertyName("presentation.bolding")]
    public bool? PresentationBolding { get; init; }

    /// <summary>
    /// Override the response presentation format.
    /// Equivalent to Vespa's <c>presentation.format</c> parameter (e.g. <c>"json"</c>).
    /// </summary>
    [JsonPropertyName("presentation.format")]
    public string? PresentationFormat { get; init; }

    /// <summary>
    /// Summary class to use for rendering hits (controls which fields are returned).
    /// Equivalent to Vespa's <c>presentation.summary</c> parameter.
    /// </summary>
    [JsonPropertyName("presentation.summary")]
    public string? PresentationSummary { get; init; }

    // ── Query Profile ──────────────────────────────────────────────────────────

    /// <summary>
    /// Named query profile to apply (overrides default query parameters with profile values).
    /// Equivalent to Vespa's <c>queryProfile</c> parameter.
    /// </summary>
    [JsonPropertyName("queryProfile")]
    public string? QueryProfile { get; init; }

    // ── Model parameters ─────────────────────────────────────────────────

    /// <summary>Limit search to specific document types (comma-separated).</summary>
    [JsonPropertyName("model.restrict")]
    public string? ModelRestrict { get; init; }

    /// <summary>Comma-separated content cluster or federated source names.</summary>
    [JsonPropertyName("model.sources")]
    public string? ModelSources { get; init; }

    /// <summary>Composite query type: weakAnd, and, or, phrase, etc.</summary>
    [JsonPropertyName("model.type")]
    public string? ModelType { get; init; }

    /// <summary>Field searched when index not specified.</summary>
    [JsonPropertyName("model.defaultIndex")]
    public string? ModelDefaultIndex { get; init; }

    /// <summary>Native Simple Query Language filter combined with <see cref="ModelQueryString"/>.</summary>
    [JsonPropertyName("model.filter")]
    public string? ModelFilter { get; init; }

    /// <summary>RFC 5646 language tag for query parsing.</summary>
    [JsonPropertyName("model.locale")]
    public string? ModelLocale { get; init; }

    /// <summary>
    /// RFC 5646 language tag (underscores allowed) — legacy alternative to
    /// <see cref="ModelLocale"/>; also see the <c>language</c> query-term annotation.
    /// </summary>
    [JsonPropertyName("model.language")]
    public string? ModelLanguage { get; init; }

    /// <summary>Result encoding (IANA character-set name, default utf-8).</summary>
    [JsonPropertyName("model.encoding")]
    public string? ModelEncoding { get; init; }

    /// <summary>The query string read by <c>userQuery()</c> and parsed according to <see cref="ModelType"/>.</summary>
    [JsonPropertyName("model.queryString")]
    public string? ModelQueryString { get; init; }

    /// <summary>Target specific content nodes (advanced).</summary>
    [JsonPropertyName("model.searchPath")]
    public string? ModelSearchPath { get; init; }

    // ── Trace / diagnostics ───────────────────────────────────────────────

    /// <summary>Content node explanation level (1-2).</summary>
    [JsonPropertyName("trace.explainLevel")]
    public int? TraceExplainLevel { get; init; }

    /// <summary>Performance profiling depth.</summary>
    [JsonPropertyName("trace.profileDepth")]
    public int? TraceProfileDepth { get; init; }

    /// <summary>Enable timing at trace level 1.</summary>
    [JsonPropertyName("trace.timestamps")]
    public bool? TraceTimestamps { get; init; }

    /// <summary>
    /// Whether to include the query in trace messages (default true).
    /// Set to false to avoid serializing very large queries into traces.
    /// </summary>
    [JsonPropertyName("trace.query")]
    public bool? TraceQuery { get; init; }

    // ── Presentation extras ───────────────────────────────────────────────

    /// <summary>Include optional timing information in response.</summary>
    [JsonPropertyName("presentation.timing")]
    public bool? PresentationTiming { get; init; }

    /// <summary>Tensor rendering format: short, long, short-value, long-value, hex.</summary>
    [JsonPropertyName("presentation.format.tensors")]
    public string? PresentationFormatTensors { get; init; }

    /// <summary>
    /// Id of a deployed page template to use for this result
    /// (used with the <c>page</c> result format).
    /// </summary>
    [JsonPropertyName("presentation.template")]
    public string? PresentationTemplate { get; init; }

    // ── Grouping tuning ───────────────────────────────────────────────────

    /// <summary>Default group limit per level (default 10).</summary>
    [JsonPropertyName("grouping.defaultMaxGroups")]
    public int? GroupingDefaultMaxGroups { get; init; }

    /// <summary>Default hits per group (default 10).</summary>
    [JsonPropertyName("grouping.defaultMaxHits")]
    public int? GroupingDefaultMaxHits { get; init; }

    /// <summary>
    /// Global cost limit for grouping (default 10000).
    /// Set this limit in a deployed query profile on Vespa versions that restrict request overrides.
    /// </summary>
    [JsonPropertyName("grouping.globalMaxGroups")]
    public int? GroupingGlobalMaxGroups { get; init; }

    /// <summary>
    /// Timezone used to offset all time-related expressions in grouping
    /// (Java timezone id, default utc).
    /// </summary>
    [JsonPropertyName("timezone")]
    public string? Timezone { get; init; }

    /// <summary>
    /// Grouping expression sent as a separate search parameter. For example,
    /// <c>all(group(category) each(output(count())))</c>.
    /// </summary>
    [JsonPropertyName("select")]
    public string? Select { get; init; }

    // ── Other ─────────────────────────────────────────────────────────────

    /// <summary>Custom search chain to invoke.</summary>
    [JsonPropertyName("searchChain")]
    public string? SearchChain { get; init; }

    /// <summary>Non-ranked Simple Query Language constraints. Prefix every term with <c>+</c> or <c>-</c>.</summary>
    [JsonPropertyName("recall")]
    public string? Recall { get; init; }

    /// <summary>Never cache this query.</summary>
    [JsonPropertyName("noCache")]
    public bool? NoCache { get; init; }

    /// <summary>Return only hit count, no documents.</summary>
    [JsonPropertyName("hitcountestimate")]
    public bool? HitCountEstimate { get; init; }

    /// <summary>Enable grouping session cache for continuation queries.</summary>
    [JsonPropertyName("groupingSessionCache")]
    public bool? GroupingSessionCache { get; init; }

    /// <summary>Probability threshold for top-K dispatch optimization.</summary>
    [JsonPropertyName("dispatch.topKProbability")]
    public double? DispatchTopKProbability { get; init; }

    /// <summary>Replace weakAnd with AND semantics.</summary>
    [JsonPropertyName("weakAnd.replace")]
    public bool? WeakAndReplace { get; init; }

    /// <summary>Target hits for wand operator.</summary>
    [JsonPropertyName("wand.hits")]
    public int? WandHits { get; init; }

    // ── Custom / dynamic parameters ─────────────────────────────────────────────

    /// <summary>
    /// Custom request parameters serialized as top-level JSON keys.
    /// Use for <c>@paramName</c> references in YQL (e.g. <c>userInput(@text)</c>)
    /// or any other arbitrary Vespa request parameter.
    /// </summary>
    [JsonIgnore]
    public ImmutableDictionary<string, object?>? CustomParameters { get; init; }

    // Source-generated metadata requires extension data outside the constructor contract.
    [JsonInclude]
    [JsonExtensionData]
    internal ImmutableDictionary<string, object?>? ExtensionData => CustomParameters;

    // ── Fluent helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sets the per-request timeout from a <see cref="TimeSpan"/>.
    /// Vespa format: milliseconds rounded to the nearest ms (e.g. <c>"500ms"</c>, <c>"5000ms"</c>).
    /// </summary>
    public VespaSearchRequest WithTimeout(TimeSpan timeout)
    {
        // Round (not truncate) and never emit "0ms", which Vespa rejects
        return this with { Timeout = $"{Math.Max(1, (long)Math.Round(timeout.TotalMilliseconds))}ms" };
    }

    /// <summary>
    /// Deduplicates results by <paramref name="field"/>, keeping the top
    /// <paramref name="size"/> hits per unique value.
    /// </summary>
    public VespaSearchRequest WithCollapse(string field, int size = 1) =>
        this with { CollapseField = field, CollapseSize = size };

    /// <summary>
    /// Adds an arbitrary top-level parameter to the search request JSON body.
    /// Useful for <c>input.query(...)</c>, <c>ranking.features.query(...)</c>,
    /// or any other Vespa parameter not covered by typed properties.
    /// </summary>
    public VespaSearchRequest WithParameter(string key, object value) =>
        this with { CustomParameters = (CustomParameters ?? ImmutableDictionary<string, object?>.Empty).SetItem(key, value) };
}

/// <summary>
/// Ranking configuration for search
/// </summary>
public sealed record RankingConfig
{
    private readonly ImmutableDictionary<string, object>? _features;
    private readonly ImmutableDictionary<string, object>? _properties;

    /// <summary>Ranking profile name</summary>
    [JsonPropertyName("profile")]
    public string? Profile { get; init; }

    /// <summary>
    /// Explicit rank feature overrides sent to the ranking expression.
    /// Keys are feature names (e.g. <c>"query(threshold)"</c>); values are scalars or tensor objects.
    /// </summary>
    [JsonPropertyName("features")]
    public IReadOnlyDictionary<string, object>? Features
    {
        get => _features;
        init => _features = SearchParameterMap.Freeze(value);
    }

    /// <summary>
    /// Additional ranking properties (overrides for rank-property values in the profile)
    /// </summary>
    [JsonPropertyName("properties")]
    public IReadOnlyDictionary<string, object>? Properties
    {
        get => _properties;
        init => _properties = SearchParameterMap.Freeze(value);
    }

    /// <summary>
    /// Number of top-N documents per node to consider for second-phase re-ranking
    /// (<c>ranking.secondPhase.rerankCount</c>). Prefer <see cref="TotalRerankCount"/>.
    /// </summary>
    [JsonPropertyName("secondPhase.rerankCount")]
    public int? RerankCount { get; init; }

    /// <summary>
    /// Number of documents to rank in the second phase in total over the queried
    /// content nodes (<c>ranking.secondPhase.totalRerankCount</c>); 0 disables second phase.
    /// </summary>
    [JsonPropertyName("secondPhase.totalRerankCount")]
    public int? TotalRerankCount { get; init; }

    /// <summary>
    /// Minimum rank score to remain a hit after second-phase reranking
    /// (<c>ranking.secondPhase.rankScoreDropLimit</c>).
    /// </summary>
    [JsonPropertyName("secondPhase.rankScoreDropLimit")]
    public double? SecondPhaseRankScoreDropLimit { get; init; }

    /// <summary>Return all computed rank features per hit (useful for debugging)</summary>
    [JsonPropertyName("listFeatures")]
    public bool? ListFeatures { get; init; }

    /// <summary>Vespa sort specification, for example <c>+year -[relevance]</c>.</summary>
    [JsonPropertyName("sorting")]
    public string? Sorting { get; init; }

    /// <summary>Enable soft timeout — return partial results when time runs out.</summary>
    [JsonPropertyName("softtimeout.enable")]
    public bool? SoftTimeoutEnable { get; init; }

    /// <summary>Fraction of timeout budget for first phase (0.0-1.0, default 0.7).</summary>
    [JsonPropertyName("softtimeout.factor")]
    public double? SoftTimeoutFactor { get; init; }

    /// <summary>Time reference for freshness-based ranking (epoch seconds).</summary>
    [JsonPropertyName("freshness")]
    public string? Freshness { get; init; }

    /// <summary>Cache the query between ranking phases.</summary>
    [JsonPropertyName("queryCache")]
    public bool? QueryCache { get; init; }

    /// <summary>
    /// Hits keeping their rank score after first phase, per node
    /// (<c>ranking.keepRankCount</c>). Prefer <see cref="TotalKeepRankCount"/>.
    /// </summary>
    [JsonPropertyName("keepRankCount")]
    public int? KeepRankCount { get; init; }

    /// <summary>
    /// Hits keeping their rank score after first phase, in total over the nodes
    /// participating in the query (<c>ranking.totalKeepRankCount</c>).
    /// </summary>
    [JsonPropertyName("totalKeepRankCount")]
    public int? TotalKeepRankCount { get; init; }

    /// <summary>Minimum first-phase score threshold. Documents scoring below are dropped.</summary>
    [JsonPropertyName("rankScoreDropLimit")]
    public double? RankScoreDropLimit { get; init; }

    /// <summary>Global phase rerank count; 0 disables global-phase reranking.</summary>
    [JsonPropertyName("globalPhase.rerankCount")]
    public int? GlobalPhaseRerankCount { get; init; }

    /// <summary>
    /// Minimum rank score to remain a hit after global-phase reranking
    /// (<c>ranking.globalPhase.rankScoreDropLimit</c>).
    /// </summary>
    [JsonPropertyName("globalPhase.rankScoreDropLimit")]
    public double? GlobalPhaseRankScoreDropLimit { get; init; }

    // ── Match phase (early termination) ───────────────────────────────────

    /// <summary>Attribute for match-phase limiting.</summary>
    [JsonPropertyName("matchPhase.attribute")]
    public string? MatchPhaseAttribute { get; init; }

    /// <summary>Maximum hits per node during match phase. Prefer <see cref="MatchPhaseTotalMaxHits"/>.</summary>
    [JsonPropertyName("matchPhase.maxHits")]
    public long? MatchPhaseMaxHits { get; init; }

    /// <summary>
    /// Maximum hits generated in total over the content nodes during the match phase
    /// (<c>ranking.matchPhase.totalMaxHits</c>); 0 disables match-phase early termination.
    /// </summary>
    [JsonPropertyName("matchPhase.totalMaxHits")]
    public long? MatchPhaseTotalMaxHits { get; init; }

    /// <summary>Keep lowest values (true) or highest (false, default).</summary>
    [JsonPropertyName("matchPhase.ascending")]
    public bool? MatchPhaseAscending { get; init; }

    /// <summary>Diversity attribute for match-phase limiting.</summary>
    [JsonPropertyName("matchPhase.diversity.attribute")]
    public string? MatchPhaseDiversityAttribute { get; init; }

    /// <summary>Minimum diversity groups.</summary>
    [JsonPropertyName("matchPhase.diversity.minGroups")]
    public int? MatchPhaseDiversityMinGroups { get; init; }

    // ── Matching parameters ──────────────────────────────────────────────

    /// <summary>Number of threads per search on content nodes.</summary>
    [JsonPropertyName("matching.numThreadsPerSearch")]
    public int? MatchingNumThreadsPerSearch { get; init; }

    /// <summary>Minimum number of hits per thread before terminating.</summary>
    [JsonPropertyName("matching.minHitsPerThread")]
    public int? MatchingMinHitsPerThread { get; init; }

    /// <summary>Fraction of corpus below which term-wise evaluation kicks in (0.0-1.0).</summary>
    [JsonPropertyName("matching.termwiseLimit")]
    public double? MatchingTermwiseLimit { get; init; }

    /// <summary>Threshold for approximate matching (0.0-1.0).</summary>
    [JsonPropertyName("matching.approximateThreshold")]
    public double? MatchingApproximateThreshold { get; init; }

    // ── Significance ─────────────────────────────────────────────────────

    /// <summary>Use the language model for significance estimation.</summary>
    [JsonPropertyName("significance.useModel")]
    public bool? SignificanceUseModel { get; init; }
}

internal static class SearchParameterMap
{
    internal static ImmutableDictionary<string, TValue>? Freeze<TValue>(IReadOnlyDictionary<string, TValue>? values) =>
        values switch
        {
            null => null,
            ImmutableDictionary<string, TValue> immutable => immutable,
            Dictionary<string, TValue> dictionary => dictionary.ToImmutableDictionary(dictionary.Comparer),
            _ => values.ToImmutableDictionary()
        };
}
