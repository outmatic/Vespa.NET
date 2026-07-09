using Vespa.Models;

namespace Vespa.Feed;

/// <summary>
/// Interface for bulk feed operations.
/// </summary>
/// <remarks>
/// <para><b>Failure semantics (all methods):</b> individual document failures are recorded in
/// <see cref="FeedResult.Errors"/> (capped at <see cref="VespaClientOptions.FeedMaxRetainedErrors"/>;
/// counts stay exact) and do not stop the feed. If the pipeline itself faults — the source
/// enumeration throws, or a progress callback throws — a <see cref="VespaFeedException"/> is
/// thrown carrying the partial <see cref="FeedResult"/>, with the original fault as its inner
/// exception. Cancellation via the caller's token surfaces as
/// <see cref="OperationCanceledException"/>.</para>
/// <para><b>Retry semantics:</b> when <see cref="VespaClientOptions.EnableRetry"/> is set, each
/// document operation that fails with HTTP 429 (<see cref="VespaTooManyRequestsException"/>,
/// honoring its <see cref="VespaTooManyRequestsException.RetryAfter"/>) or HTTP 503
/// (<see cref="VespaServerException"/>) is re-sent with exponential backoff and jitter, up to
/// <see cref="VespaClientOptions.MaxRetryAttempts"/> extra attempts
/// (base delay: <see cref="VespaClientOptions.FeedRetryBaseDelay"/>). Clients registered via DI
/// additionally retry transient responses at the HTTP-handler level; when both are active the
/// handler retries first, so each feed-level attempt may itself have been retried at the HTTP
/// level.</para>
/// </remarks>
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
    /// <param name="onProgress">
    /// Optional callback invoked after each document outcome (success or failure).
    /// It is invoked <b>concurrently</b> from multiple consumer tasks — it must be thread-safe.
    /// <see cref="FeedProgress.ProcessedCount"/> is unique and monotonically increasing per
    /// invocation. A throwing callback faults the pipeline (see the interface remarks).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// If the source enumeration throws mid-feed, documents already buffered in the channel are
    /// still sent before the fault surfaces — their outcomes are included in the partial
    /// <see cref="FeedResult"/> carried by the resulting <see cref="VespaFeedException"/>.
    /// </remarks>
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
/// Progress snapshot emitted by <see cref="IFeedOperations.FeedAsync{T}"/> after each document outcome.
/// </summary>
/// <param name="ProcessedCount">
/// Number of documents whose outcome had been recorded when this snapshot was taken —
/// unique and monotonically increasing across invocations (success + failure).
/// </param>
/// <param name="SuccessCount">Successful documents so far.</param>
/// <param name="FailureCount">Failed documents so far.</param>
/// <param name="LastDocumentId">The document this snapshot was emitted for.</param>
/// <remarks>
/// Snapshots are emitted concurrently, so observed callback order may not match
/// <paramref name="ProcessedCount"/> order; within a snapshot,
/// <c>SuccessCount + FailureCount &gt;= ProcessedCount</c> always holds.
/// </remarks>
public sealed record FeedProgress(int ProcessedCount, int SuccessCount, int FailureCount, string LastDocumentId);
