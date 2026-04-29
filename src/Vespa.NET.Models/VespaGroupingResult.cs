namespace Vespa.Models;

/// <summary>
/// A named list of groups at one level of a Vespa grouping result
/// (corresponds to a <c>grouplist:fieldname</c> node in the response)
/// </summary>
public sealed record VespaGroupList(
    string Label,
    IReadOnlyList<VespaGroup> Groups);

/// <summary>
/// A single group bucket in a Vespa grouping result
/// </summary>
public sealed record VespaGroup(
    string Value,
    IReadOnlyDictionary<string, double> Aggregations,
    IReadOnlyList<VespaGroupList> SubGroups);

/// <summary>
/// Combined response from a grouping search: regular hits + grouping aggregations.
/// When <see cref="Continuation"/> is non-null, pass it as
/// <c>VespaSearchRequest.GroupingContinuation</c> to retrieve the next page of groups.
/// </summary>
public sealed record GroupingSearchResponse<T>(
    IReadOnlyList<SearchHit<T>> Hits,
    IReadOnlyList<VespaGroupList> GroupingResults,
    long TotalCount,
    TimingInfo? Timing = null,
    string? Continuation = null
) where T : class;
