using System.Collections.Concurrent;
using Vespa.Models;

namespace Vespa.Feed;

/// <summary>
/// Interface for bulk feed operations
/// </summary>
public interface IFeedOperations
{
    /// <summary>
    /// Bulk insert/update multiple documents
    /// </summary>
    Task<FeedResult> BulkPutAsync<T>(
        IEnumerable<FeedDocument<T>> documents,
        string documentType,
        string? @namespace = null,
        int maxConcurrency = 10,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Bulk partial-update multiple documents. Each request carries explicit Vespa field
    /// operations (<see cref="FieldOperation"/>) — Vespa rejects raw values on
    /// <c>/document/v1 PUT</c>, so partial documents are not accepted here.
    /// </summary>
    Task<FeedResult> BulkUpdateAsync(
        IEnumerable<BulkFieldUpdate> updates,
        string documentType,
        string? @namespace = null,
        bool createIfMissing = false,
        int maxConcurrency = 10,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Bulk delete multiple documents
    /// </summary>
    Task<FeedResult> BulkDeleteAsync(
        IEnumerable<string> documentIds,
        string documentType,
        string? @namespace = null,
        int maxConcurrency = 10,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// High-throughput streaming feed pipeline. Reads documents from an <see cref="IAsyncEnumerable{T}"/>
    /// and writes them concurrently using a bounded channel with HTTP/2 multiplexing.
    /// Unlike <see cref="BulkPutAsync{T}"/>, the input is not materialized into a list —
    /// documents are consumed on-demand with backpressure.
    /// </summary>
    /// <param name="documents">Streaming source of documents (e.g. from a database cursor).</param>
    /// <param name="documentType">Vespa document type.</param>
    /// <param name="namespace">Optional namespace override.</param>
    /// <param name="maxConcurrency">Number of parallel HTTP/2 streams (default 64).</param>
    /// <param name="boundedCapacity">Channel buffer size for backpressure (default 256).</param>
    /// <param name="onProgress">Optional callback invoked after each document (success or failure).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<FeedResult> FeedAsync<T>(
        IAsyncEnumerable<FeedDocument<T>> documents,
        string documentType,
        string? @namespace = null,
        int maxConcurrency = 64,
        int boundedCapacity = 256,
        Action<FeedProgress>? onProgress = null,
        CancellationToken cancellationToken = default
    ) where T : class;
}

/// <summary>
/// Represents a document for feeding
/// </summary>
public sealed record FeedDocument<T> where T : class
{
    public string Id { get; init; } = string.Empty;
    public T Fields { get; init; } = default!;

    /// <summary>
    /// Optional document selection condition for this document.
    /// When set, the PUT will only succeed if the condition is met (HTTP 412 otherwise).
    /// Example: <c>"music.year > 2000"</c>
    /// </summary>
    public string? Condition { get; init; }
}

/// <summary>
/// Single request in a <see cref="IFeedOperations.BulkUpdateAsync"/> batch.
/// Carries the document ID plus a dictionary of Vespa field operations (produced by
/// <see cref="FieldOp"/> helpers), matching the shape expected by <c>PUT /document/v1</c>.
/// </summary>
public sealed record BulkFieldUpdate
{
    public string Id { get; init; } = string.Empty;

    public Dictionary<string, FieldOperation> FieldOperations { get; init; } = [];

    /// <summary>
    /// Optional document selection condition for this update.
    /// When set, the update only succeeds if the condition is met (HTTP 412 otherwise).
    /// </summary>
    public string? Condition { get; init; }
}

/// <summary>
/// Result of a bulk feed operation
/// </summary>
public sealed class FeedResult
{
    private int _successCount;
    private int _failureCount;

    public int TotalDocuments { get; set; }
    public int SuccessCount => _successCount;
    public int FailureCount => _failureCount;
    public ConcurrentQueue<FeedError> Errors { get; set; } = new();
    public TimeSpan Duration { get; set; }

    public bool IsSuccess => FailureCount == 0;
    public double SuccessRate => TotalDocuments > 0 ? (double)SuccessCount / TotalDocuments : 0;

    /// <summary>
    /// Thread-safe increment of success count
    /// </summary>
    public void IncrementSuccess() => Interlocked.Increment(ref _successCount);

    /// <summary>
    /// Thread-safe increment of failure count
    /// </summary>
    public void IncrementFailure() => Interlocked.Increment(ref _failureCount);
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

/// <summary>
/// Progress snapshot emitted by <see cref="IFeedOperations.FeedAsync{T}"/> after each document.
/// </summary>
public sealed record FeedProgress(int SuccessCount, int FailureCount, string LastDocumentId);
