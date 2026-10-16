using System.Text.Json;

namespace Vespa.Models;

/// <summary>
/// A named list of groups at one level of a Vespa grouping result
/// (corresponds to a <c>grouplist:fieldname</c> node in the response)
/// </summary>
public sealed record VespaGroupList(
    string Label,
    IReadOnlyList<VespaGroup> Groups);

/// <summary>
/// A single group bucket in a Vespa grouping result.
/// <see cref="Aggregations"/> holds the numeric aggregation outputs; non-numeric
/// outputs (e.g. <c>toraw</c>/<c>tostring</c> expressions) are only available in
/// <see cref="RawAggregations"/>.
/// </summary>
public sealed record VespaGroup(
    string Value,
    IReadOnlyDictionary<string, double> Aggregations,
    IReadOnlyList<VespaGroupList> SubGroups)
{
    /// <summary>Full group identity returned by Vespa, including bucket and root identities.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Whether this group contains values outside all predefined buckets.</summary>
    public bool IsRestBucket => Id == "group:null";

    /// <summary>
    /// All aggregation outputs of this group as raw JSON values, keyed by the
    /// aggregation expression (or its <c>as(label)</c>). Includes non-numeric
    /// outputs that <see cref="Aggregations"/> cannot represent.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> RawAggregations { get; init; } =
        new Dictionary<string, JsonElement>();

    /// <summary>
    /// Deserializes an aggregation from its original JSON, preserving integer and
    /// decimal precision. Throws <see cref="KeyNotFoundException"/> when the name
    /// is absent and <see cref="JsonException"/> when the value cannot be converted.
    /// JSON null is returned as null for nullable or reference types.
    /// </summary>
    public TValue? GetAggregation<TValue>(string name, JsonSerializerOptions? options = null) =>
        GroupingAggregation.Get<TValue>(RawAggregations, name, options);

    /// <summary>
    /// Tries to deserialize an aggregation from its original JSON. Returns false
    /// for a missing name or a JSON conversion error, including integer and decimal overflow.
    /// JSON null succeeds for nullable or reference types. Unsupported types and
    /// invalid serializer configuration still throw.
    /// </summary>
    public bool TryGetAggregation<TValue>(string name, out TValue? value, JsonSerializerOptions? options = null) =>
        GroupingAggregation.TryGet(RawAggregations, name, out value, options);

    /// <summary>
    /// Document summaries returned inside this group when the grouping expression
    /// requests them via <c>each(output(summary()))</c> (<c>hitlist:*</c> children).
    /// Each element is a <c>SearchHit&lt;T&gt;</c> for the <c>T</c> used in the
    /// <c>GroupByAsync&lt;T&gt;</c> call — use <see cref="GetHits{T}"/> for typed access.
    /// </summary>
    public IReadOnlyList<object> Hits { get; init; } = [];

    /// <summary>
    /// Returns <see cref="Hits"/> as typed <see cref="SearchHit{T}"/> instances.
    /// <typeparamref name="T"/> must be the type used in the originating
    /// <c>GroupByAsync&lt;T&gt;</c> call; throws <see cref="InvalidCastException"/> otherwise.
    /// </summary>
    public IReadOnlyList<SearchHit<T>> GetHits<T>() where T : class =>
        [.. Hits.Cast<SearchHit<T>>()];
}

/// <summary>
/// Combined response from a grouping search: regular hits + grouping aggregations.
/// When <see cref="ContinuationTokens"/> is non-null there are more pages: pass the
/// tokens back via the YQL <c>continuations</c> annotation
/// (<c>| {{ 'continuations':[…] }}all(…)</c>) — <c>GroupByStreamAsync</c> does this
/// automatically. The list contains the root <c>this</c> token followed by the
/// group lists' <c>next</c> tokens.
/// </summary>
public sealed record GroupingSearchResponse<T>(
    IReadOnlyList<SearchHit<T>> Hits,
    IReadOnlyList<VespaGroupList> GroupingResults,
    long TotalCount,
    TimingInfo? Timing = null,
    IReadOnlyList<string>? ContinuationTokens = null
) where T : class
{
    /// <summary>
    /// Root group for each grouping expression, retaining root-level aggregations and
    /// hit lists. Their subgroups are also exposed through <see cref="GroupingResults"/>.
    /// </summary>
    public IReadOnlyList<VespaGroup> GroupingRoots { get; init; } = [];

    /// <summary>Coverage and degradation information, including soft timeouts.</summary>
    public Coverage? Coverage { get; init; }

    /// <summary>Backend errors, which may accompany partial results with HTTP 200.</summary>
    public IReadOnlyList<VespaError>? Errors { get; init; }

    /// <summary>Query trace returned when tracing is requested.</summary>
    public TraceInfo? Trace { get; init; }

    /// <summary>
    /// Projects this response into a typed grouping hierarchy. Group summaries
    /// become <see cref="SearchHit{T}"/> lists at every level, with the original
    /// hits, aggregations, metadata and continuation tokens retained.
    /// Performs no HTTP requests and leaves this response unchanged.
    /// </summary>
    public TypedGroupingSearchResponse<T> AsTyped()
    {
        var projection = new TypedGroupingProjection<T>();
        return new(Hits, [.. GroupingResults.Select(projection.List)], TotalCount, Timing, ContinuationTokens)
        {
            GroupingRoots = [.. GroupingRoots.Select(projection.Group)],
            Coverage = Coverage,
            Errors = Errors,
            Trace = Trace
        };
    }
}
