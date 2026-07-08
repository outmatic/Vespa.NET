namespace Vespa.Models;

/// <summary>
/// Optional parameters for selection-based document operations
/// (<c>UpdateBySelectionAsync</c>, <c>DeleteBySelectionAsync</c>, <c>CopyBySelectionAsync</c>)
/// that map to Vespa query parameters on <c>/document/v1</c>.
/// </summary>
public sealed record SelectionRequestOptions
{
    /// <summary>
    /// Target processing time per chunk before Vespa returns a continuation token.
    /// Maps to the <c>timeChunk</c> query parameter (Vespa default: 60s).
    /// </summary>
    public TimeSpan? TimeChunk { get; init; }

    /// <summary>
    /// Bucket space for the operation (e.g. <c>default</c>, <c>global</c>).
    /// Maps to the <c>bucketSpace</c> query parameter.
    /// </summary>
    public string? BucketSpace { get; init; }

    /// <summary>
    /// Per-request timeout applied as a Vespa query parameter (distinct from the
    /// HTTP client timeout). Maps to the <c>timeout</c> query parameter
    /// (value serialized as <c>{ms}ms</c>).
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Diagnostic trace level (0-9). Maps to the <c>tracelevel</c> query parameter.
    /// </summary>
    public int? TraceLevel { get; init; }

    /// <summary>
    /// Builds query parameter tuples for use in URL construction.
    /// <c>continuation</c> is NOT surfaced here — selection operations manage it internally.
    /// </summary>
    public IEnumerable<(string Key, string? Value)> ToQueryParams()
    {
        if (TimeChunk.HasValue)
            yield return ("timeChunk", VespaQueryValues.ToMilliseconds(TimeChunk.Value));
        if (BucketSpace is not null)
            yield return ("bucketSpace", BucketSpace);
        if (Timeout.HasValue)
            yield return ("timeout", VespaQueryValues.ToMilliseconds(Timeout.Value));
        if (TraceLevel.HasValue)
            yield return ("tracelevel", TraceLevel.Value.ToString());
    }
}
