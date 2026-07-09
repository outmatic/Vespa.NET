using System.Collections.Concurrent;

namespace Vespa.Models;

/// <summary>
/// Result of a bulk feed operation. Populated by the feed pipeline;
/// read-only for consumers.
/// </summary>
public sealed class FeedResult
{
    /// <summary>Default maximum number of <see cref="FeedError"/> entries retained in <see cref="Errors"/>.</summary>
    public const int DefaultMaxRetainedErrors = 1000;

    private readonly ConcurrentQueue<FeedError> _errors = new();
    private readonly int _maxRetainedErrors;
    private int _successCount;
    private int _failureCount;
    private int _errorCount;
    private int _hadIgnoredFields;

    /// <summary>
    /// Creates a result that retains at most <paramref name="maxRetainedErrors"/> entries in
    /// <see cref="Errors"/>. Counts (<see cref="FailureCount"/>) are always exact regardless
    /// of the cap; <see cref="ErrorsTruncated"/> reports whether entries were dropped.
    /// </summary>
    public FeedResult(int maxRetainedErrors = DefaultMaxRetainedErrors)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetainedErrors);
        _maxRetainedErrors = maxRetainedErrors;
    }

    /// <summary>Total number of documents read from the source.</summary>
    public int TotalDocuments { get; internal set; }

    /// <summary>Number of documents fed successfully.</summary>
    public int SuccessCount => _successCount;

    /// <summary>Number of documents that failed (exact, unaffected by the error cap).</summary>
    public int FailureCount => _failureCount;

    /// <summary>
    /// Per-document error details, capped at the <c>maxRetainedErrors</c> the result was
    /// created with (first errors win). See <see cref="ErrorsTruncated"/>.
    /// </summary>
    public IReadOnlyCollection<FeedError> Errors => _errors;

    /// <summary>
    /// <see langword="true"/> when more errors occurred than <see cref="Errors"/> retains.
    /// <see cref="FailureCount"/> stays exact either way.
    /// </summary>
    public bool ErrorsTruncated => Volatile.Read(ref _errorCount) > _maxRetainedErrors;

    /// <summary>
    /// <see langword="true"/> when at least one response signalled ignored fields
    /// (<c>X-Vespa-Ignored-Fields</c> header) — typically a document/schema mismatch
    /// with <c>ignore-undefined-fields</c> enabled.
    /// </summary>
    public bool HadIgnoredFields => _hadIgnoredFields != 0;

    /// <summary>Total elapsed pipeline time.</summary>
    public TimeSpan Duration { get; internal set; }

    /// <summary><see langword="true"/> if no document failed.</summary>
    public bool IsSuccess => FailureCount == 0;

    /// <summary>Fraction of <see cref="TotalDocuments"/> fed successfully.</summary>
    public double SuccessRate => TotalDocuments > 0 ? (double)SuccessCount / TotalDocuments : 0;

    internal void IncrementSuccess() => Interlocked.Increment(ref _successCount);

    internal void IncrementFailure() => Interlocked.Increment(ref _failureCount);

    internal void AddError(FeedError error)
    {
        if (Interlocked.Increment(ref _errorCount) <= _maxRetainedErrors)
            _errors.Enqueue(error);
    }

    /// <summary>Records that a response had ignored fields; returns <see langword="true"/> only the first time.</summary>
    internal bool MarkIgnoredFields() => Interlocked.Exchange(ref _hadIgnoredFields, 1) == 0;
}

/// <summary>
/// Error details for a failed feed operation
/// </summary>
public sealed record FeedError
{
    public string DocumentId { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int? StatusCode { get; init; }
}
