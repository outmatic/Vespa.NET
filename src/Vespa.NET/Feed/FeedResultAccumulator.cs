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

    /// <summary>Counts a failed operation and retains its details up to the configured limit.</summary>
    public void RecordFailure(string documentId, Exception exception, FeedOperationKind? operationKind = null,
        string? documentType = null, string? documentNamespace = null, int? operationIndex = null)
    {
        // Reading a custom exception's message can throw. Count the failed operation
        // first so the partial result still reports it.
        Interlocked.Increment(ref _failureCount);
        var error = new FeedError
        {
            DocumentId = documentId,
            Message = exception.Message,
            StatusCode = (exception as VespaException)?.StatusCode,
            OperationKind = operationKind,
            DocumentType = documentType,
            Namespace = documentNamespace,
            OperationIndex = operationIndex
        };
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
