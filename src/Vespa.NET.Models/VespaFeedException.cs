namespace Vespa.Models;

/// <summary>
/// Thrown when a bulk feed pipeline faults (source enumeration error, throwing progress
/// callback, or an unexpected internal failure) — as opposed to individual document
/// failures, which are recorded in <see cref="FeedResult.Errors"/> without stopping the feed.
/// <para>
/// <see cref="PartialResult"/> carries the progress made before the fault (counts, retained
/// errors, duration), so partially-completed work is never lost. The original fault is the
/// <see cref="Exception.InnerException"/>; when several independent faults occurred it is an
/// <see cref="AggregateException"/>.
/// </para>
/// <para>
/// Cancellation via the caller's <see cref="CancellationToken"/> is not wrapped: it still
/// surfaces as <see cref="OperationCanceledException"/>.
/// </para>
/// </summary>
public sealed class VespaFeedException(string message, FeedResult partialResult, Exception innerException)
    : VespaException(message, innerException)
{
    /// <summary>Feed progress accumulated before the pipeline faulted.</summary>
    public FeedResult PartialResult { get; } = partialResult;
}
