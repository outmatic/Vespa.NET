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
    IReadOnlyList<VespaGroupList> SubGroups)
{
    /// <summary>
    /// Document summaries returned inside this group when the grouping expression
    /// requests them via <c>each(output(summary()))</c> (<c>hitlist:*</c> children).
    /// Each element is a <c>SearchHit&lt;T&gt;</c> for the <c>T</c> used in the
    /// <c>GroupByAsync&lt;T&gt;</c> call — cast accordingly.
    /// </summary>
    public IReadOnlyList<object> Hits { get; init; } = [];
}

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
