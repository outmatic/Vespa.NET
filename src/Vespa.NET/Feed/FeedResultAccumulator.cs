using System.Collections.Concurrent;
using Vespa.Models;

namespace Vespa.Feed;

/// <summary>
/// Thread-safe accumulator for the feed pipeline's counters and errors.
/// Snapshot into an immutable <see cref="FeedResult"/> via <see cref="ToResult"/>.
/// </summary>
internal sealed class FeedResultAccumulator(int maxRetainedErrors)
{
    private readonly ConcurrentQueue<FeedError> _errors = new();
    private int _totalDocuments;
    private int _successCount;
    private int _failureCount;
    private int _errorCount;
    private int _hadIgnoredFields;

    public int SuccessCount => Volatile.Read(ref _successCount);

    public int FailureCount => Volatile.Read(ref _failureCount);

    public void SetTotalDocuments(int count) => Volatile.Write(ref _totalDocuments, count);

    public void IncrementSuccess() => Interlocked.Increment(ref _successCount);

    public void IncrementFailure() => Interlocked.Increment(ref _failureCount);

    /// <summary>Records an error, retaining at most <c>maxRetainedErrors</c> entries (first errors win).</summary>
    public void AddError(FeedError error)
    {
        if (Interlocked.Increment(ref _errorCount) <= maxRetainedErrors)
            _errors.Enqueue(error);
    }

    /// <summary>Records that a response had ignored fields; returns <see langword="true"/> only the first time.</summary>
    public bool MarkIgnoredFields() => Interlocked.Exchange(ref _hadIgnoredFields, 1) == 0;

    public FeedResult ToResult(TimeSpan duration) => new()
    {
        TotalDocuments = Volatile.Read(ref _totalDocuments),
        SuccessCount = SuccessCount,
        FailureCount = FailureCount,
        Errors = [.. _errors],
        ErrorsTruncated = Volatile.Read(ref _errorCount) > maxRetainedErrors,
        HadIgnoredFields = Volatile.Read(ref _hadIgnoredFields) != 0,
        Duration = duration
    };
}
