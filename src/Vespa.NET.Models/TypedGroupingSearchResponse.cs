using System.Text.Json;

namespace Vespa.Models;

/// <summary>A named list of groups with summaries typed as <typeparamref name="T"/>.</summary>
public sealed record VespaGroupList<T>(
    string Label,
    IReadOnlyList<VespaGroup<T>> Groups) where T : class;

/// <summary>
/// A group bucket whose summaries and nested group lists retain the document type.
/// Created by <see cref="GroupingSearchResponse{T}.AsTyped"/>.
/// </summary>
public sealed record VespaGroup<T>(
    string Value,
    IReadOnlyDictionary<string, double> Aggregations,
    IReadOnlyList<VespaGroupList<T>> SubGroups) where T : class
{
    /// <summary>Full group identity, including bucket and root identities.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Whether this group contains values outside all predefined buckets.</summary>
    public bool IsRestBucket => Id == "group:null";

    /// <summary>
    /// All outputs in their original JSON representation. Use
    /// <see cref="GetAggregation{TValue}"/> for exact integers, decimals and structured values;
    /// <see cref="Aggregations"/> remains a double-valued convenience view.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> RawAggregations { get; init; } =
        new Dictionary<string, JsonElement>();

    /// <summary>Document summaries requested with <c>each(output(summary()))</c>.</summary>
    public IReadOnlyList<SearchHit<T>> Hits { get; init; } = [];

    /// <inheritdoc cref="VespaGroup.GetAggregation{TValue}"/>
    public TValue? GetAggregation<TValue>(string name, JsonSerializerOptions? options = null) =>
        GroupingAggregation.Get<TValue>(RawAggregations, name, options);

    /// <inheritdoc cref="VespaGroup.TryGetAggregation{TValue}"/>
    public bool TryGetAggregation<TValue>(string name, out TValue? value, JsonSerializerOptions? options = null) =>
        GroupingAggregation.TryGet(RawAggregations, name, out value, options);
}

/// <summary>
/// A typed view of a grouping search, including regular hits, root groups and
/// nested group lists. Obtain one from <see cref="GroupingSearchResponse{T}.AsTyped"/>,
/// including on each page returned by <c>GroupByStreamAsync</c>.
/// </summary>
public sealed record TypedGroupingSearchResponse<T>(
    IReadOnlyList<SearchHit<T>> Hits,
    IReadOnlyList<VespaGroupList<T>> GroupingResults,
    long TotalCount,
    TimingInfo? Timing = null,
    IReadOnlyList<string>? ContinuationTokens = null) where T : class
{
    /// <summary>Root groups, retaining their outputs, summaries and subgroups.</summary>
    public IReadOnlyList<VespaGroup<T>> GroupingRoots { get; init; } = [];

    /// <summary>Coverage and degradation information, including soft timeouts.</summary>
    public Coverage? Coverage { get; init; }

    /// <summary>Backend errors, which may accompany partial results with HTTP 200.</summary>
    public IReadOnlyList<VespaError>? Errors { get; init; }

    /// <summary>Query trace returned when tracing is requested.</summary>
    public TraceInfo? Trace { get; init; }
}

internal sealed class TypedGroupingProjection<T> where T : class
{
    private readonly Dictionary<VespaGroupList, VespaGroupList<T>> _lists = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<VespaGroup, VespaGroup<T>> _groups = new(ReferenceEqualityComparer.Instance);

    internal VespaGroupList<T> List(VespaGroupList source)
    {
        if (_lists.TryGetValue(source, out var list))
            return list;

        list = new(source.Label, [.. source.Groups.Select(Group)]);
        _lists.Add(source, list);
        return list;
    }

    internal VespaGroup<T> Group(VespaGroup source)
    {
        if (_groups.TryGetValue(source, out var group))
            return group;

        group = new(source.Value, source.Aggregations, [.. source.SubGroups.Select(List)])
        {
            Id = source.Id,
            Hits = source.GetHits<T>(),
            RawAggregations = source.RawAggregations
        };
        _groups.Add(source, group);
        return group;
    }
}
