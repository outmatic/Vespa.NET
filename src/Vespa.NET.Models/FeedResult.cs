namespace Vespa.Models;

/// <summary>
/// Immutable result of a bulk feed operation, built by the feed pipeline when it completes
/// (or carried by <see cref="VespaFeedException.PartialResult"/> when it faults).
/// </summary>
public sealed class FeedResult
{
    /// <summary>Default maximum number of <see cref="FeedError"/> entries retained in <see cref="Errors"/>.</summary>
    public const int DefaultMaxRetainedErrors = 1000;

    /// <summary>Total feed entries accepted from the source, counting each mutation separately.</summary>
    public int TotalDocuments { get; init; }

    /// <summary>Total operations accepted from the source (alias of <see cref="TotalDocuments"/>).</summary>
    public int TotalOperations => TotalDocuments;

    /// <summary>Number of documents fed successfully.</summary>
    public int SuccessCount { get; init; }

    /// <summary>Number of documents that failed (exact, unaffected by the error cap).</summary>
    public int FailureCount { get; init; }

    /// <summary>
    /// Per-document error details, capped at the configured maximum
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

    /// <summary><see langword="true"/> if no document failed.</summary>
    public bool IsSuccess => FailureCount == 0;

    /// <summary>Fraction of <see cref="TotalDocuments"/> fed successfully.</summary>
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
