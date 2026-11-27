using System.Diagnostics;
using Vespa.Models;

namespace Vespa.Feed;

public sealed partial class FeedOperations
{
    private static readonly TimeSpan MaxRetryBackoff = TimeSpan.FromSeconds(30);

    // Returns a per-item failure for the caller to record with its own feed metadata.
    private async Task<Exception?> ExecuteFeedItemAsync<TItem>(
        TItem item,
        string documentId,
        Func<TItem, CancellationToken, Task<VespaResponse>> operation,
        FeedResultAccumulator accumulator,
        CancellationToken cancellationToken,
        (string? ExpectedType, string DocumentType)? batchTarget = null)
    {
        try
        {
            if (batchTarget is { } target)
                FeedEntryCondition.ValidateTarget(documentId, target.ExpectedType, target.DocumentType);

            // The DI HTTP handler retries first. Resend only after it has given up on backpressure.
            VespaResponse response;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    response = await operation(item, cancellationToken);
                    break;
                }
                catch (VespaException ex) when (
                    _options.EnableRetry && attempt < _options.MaxRetryAttempts && IsRetryableFeedError(ex))
                {
                    var delay = ComputeRetryDelay(_options.FeedRetryBaseDelay, attempt,
                        (ex as VespaTooManyRequestsException)?.RetryAfter);
                    if (_logger is not null)
                        LogDocumentFeedRetry(_logger, documentId, attempt + 1, delay.TotalMilliseconds, ex);
                    await Task.Delay(delay, cancellationToken);
                }
            }

            if (response.HadIgnoredFields && accumulator.MarkIgnoredFields() && _logger is not null)
                LogIgnoredFields(_logger, documentId);
            accumulator.IncrementSuccess();
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            return ex;
        }
    }

    private static async Task<FeedResult> CompletePipelineAsync(
        IReadOnlyList<Task> tasks,
        FeedResultAccumulator accumulator,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        try
        {
            // Await all producers and consumers before reporting a failure, so
            // no HTTP operation is abandoned.
            await Task.WhenAll(tasks);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            if (cancellationToken.IsCancellationRequested)
            {
                if (exception is OperationCanceledException)
                    throw;
                throw new OperationCanceledException("The feed pipeline was canceled.", exception, cancellationToken);
            }

            // WhenAll exposes one exception. Prefer the actual faults over the
            // linked cancellation they caused, aggregating independent failures.
            var faults = tasks.Where(task => task.IsFaulted)
                .SelectMany(task => task.Exception!.InnerExceptions)
                .Where(error => error is not OperationCanceledException).ToList();
            var cause = faults switch
            {
                [] => exception,
                [var single] => single,
                _ => new AggregateException(faults)
            };
            var partialResult = accumulator.ToResult(stopwatch.Elapsed);
            throw new VespaFeedException(
                $"Feed pipeline faulted after {partialResult.SuccessCount} succeeded and " +
                $"{partialResult.FailureCount} failed (see PartialResult): {cause.Message}", partialResult, cause);
        }

        stopwatch.Stop();
        return accumulator.ToResult(stopwatch.Elapsed);
    }

    /// <summary>429 (always transient backpressure) and 503 (overload/unavailable) are retryable.</summary>
    private static bool IsRetryableFeedError(VespaException ex) =>
        ex is VespaTooManyRequestsException or VespaServerException { StatusCode: 503 };

    /// <summary>
    /// Server-suggested <c>Retry-After</c> wins; otherwise exponential backoff
    /// (base × 2^attempt, capped) with ±20% jitter.
    /// </summary>
    private static TimeSpan ComputeRetryDelay(TimeSpan baseDelay, int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } serverDelay && serverDelay > TimeSpan.Zero)
            return serverDelay;

        var exponential = baseDelay * Math.Pow(2, Math.Min(attempt, 10));
        var capped = exponential > MaxRetryBackoff ? MaxRetryBackoff : exponential;
        return capped * (0.8 + Random.Shared.NextDouble() * 0.4);
    }
}
