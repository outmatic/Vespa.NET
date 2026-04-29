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
    /// When <see langword="true"/>, ask Vespa to stream the response as JSONL
    /// (one object per line) and visit more buckets per HTTP round-trip, reducing
    /// latency on large selections. The client still loops on the returned
    /// continuation token; streaming is a transport optimization, not server-side
    /// auto-resume.
    /// <para>Supported on <c>UpdateBySelection</c> and <c>DeleteBySelection</c>
    /// only — Vespa does not accept <c>stream=true</c> on
    /// <c>CopyBySelection</c>; setting this flag on a copy throws
    /// <see cref="ArgumentException"/>.</para>
    /// </summary>
    public bool? Stream { get; init; }

    /// <summary>
    /// Builds query parameter tuples for use in URL construction.
    /// <c>continuation</c> and <c>stream</c> are NOT surfaced here — selection
    /// operations manage the former internally and the latter flips the response
    /// parser separately.
    /// </summary>
    public IEnumerable<(string Key, string? Value)> ToQueryParams()
    {
        if (TimeChunk.HasValue)
            yield return ("timeChunk", $"{(long)TimeChunk.Value.TotalMilliseconds}ms");
        if (BucketSpace is not null)
            yield return ("bucketSpace", BucketSpace);
        if (Timeout.HasValue)
            yield return ("timeout", $"{(long)Timeout.Value.TotalMilliseconds}ms");
        if (TraceLevel.HasValue)
            yield return ("tracelevel", TraceLevel.Value.ToString());
    }
}
