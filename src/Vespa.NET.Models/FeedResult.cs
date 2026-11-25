namespace Vespa.Models;

/// <summary>
/// Immutable result of a bulk feed operation, built by the feed pipeline when it completes
/// (or carried by <see cref="VespaFeedException.PartialResult"/> when it faults).
/// </summary>
public sealed class FeedResult
{
    /// <summary>Default maximum number of <see cref="FeedError"/> entries retained in <see cref="Errors"/>.</summary>
    public const int DefaultMaxRetainedErrors = 1000;

    /// <summary>Total feed entries accepted from the source. This property sets the count returned by <see cref="TotalOperations"/>.</summary>
    public int TotalDocuments { get; init; }

    /// <summary>
    /// Total operations accepted from the source. Each mutation counts separately, including repeated
    /// mutations of the same document. Retries count as part of the original operation.
    /// </summary>
    public int TotalOperations => TotalDocuments;

    /// <summary>Number of successful operations.</summary>
    public int SuccessCount { get; init; }

    /// <summary>Number of failed operations (exact, unaffected by the error cap).</summary>
    public int FailureCount { get; init; }

    /// <summary>
    /// Per-operation error details, capped at the configured maximum
    /// (first errors win). See <see cref="ErrorsTruncated"/>.
    /// </summary>
    public IReadOnlyList<FeedError> Errors { get; init; } = [];

    /// <summary>
    /// <see langword="true"/> when more errors occurred than <see cref="Errors"/> retains.
    /// <see cref="FailureCount"/> stays exact either way.
    /// </summary>
    public bool ErrorsTruncated { get; init; }

    /// <summary>
    /// <see langword="true"/> when at least one response signalled ignored fields
    /// (<c>X-Vespa-Ignored-Fields</c> header) — typically a document/schema mismatch
    /// with <c>ignore-undefined-fields</c> enabled.
    /// </summary>
    public bool HadIgnoredFields { get; init; }

    /// <summary>Total elapsed pipeline time.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary><see langword="true"/> if no operation failed.</summary>
    public bool IsSuccess => FailureCount == 0;

    /// <summary>Fraction of <see cref="TotalOperations"/> that succeeded.</summary>
    public double SuccessRate => TotalDocuments > 0 ? (double)SuccessCount / TotalDocuments : 0;
}

/// <summary>
/// Error details for a failed feed operation
/// </summary>
public sealed record FeedError
{
    public string DocumentId { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int? StatusCode { get; init; }

    /// <summary>Mutation kind for a mixed-feed error; null for legacy bulk APIs.</summary>
    public FeedOperationKind? OperationKind { get; init; }

    /// <summary>Target schema for a mixed-feed error.</summary>
    public string? DocumentType { get; init; }

    /// <summary>Resolved target namespace for a mixed-feed error.</summary>
    public string? Namespace { get; init; }

    /// <summary>Zero-based position in the mixed-feed source, independent of completion order.</summary>
    public int? OperationIndex { get; init; }
}
