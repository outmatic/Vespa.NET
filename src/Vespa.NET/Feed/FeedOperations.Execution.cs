using System.Diagnostics;
using Vespa.Models;

namespace Vespa.Feed;

public sealed partial class FeedOperations
{
    private static readonly TimeSpan MaxRetryBackoff = TimeSpan.FromSeconds(30);

    // The DI HTTP handler retries first; this loop resends an operation only after
    // that handler has given up on Vespa's transient backpressure signals.
    private async Task<VespaResponse> ExecuteWithRetryAsync<TItem>(
        TItem item,
        string documentId,
        Func<TItem, CancellationToken, Task<VespaResponse>> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation(item, cancellationToken);
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
