using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Vespa.Models;

namespace Vespa.Feed;

public sealed partial class FeedOperations
{
    public Task<FeedResult> FeedAsync(
        IEnumerable<FeedOperation> operations,
        FeedOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return FeedAsync(operations.ToAsyncEnumerable(cancellationToken), options, cancellationToken);
    }

    public Task<FeedResult> FeedAsync(
        IAsyncEnumerable<FeedOperation> operations,
        FeedOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        options ??= new();
        options.Validate();
        return ExecuteMixedPipelineAsync(operations, options, cancellationToken);
    }

    private async Task<FeedResult> ExecuteMixedPipelineAsync(
        IAsyncEnumerable<FeedOperation> operations,
        FeedOptions options,
        CancellationToken cancellationToken)
    {
        if (_logger is not null)
            LogPipelineStart(_logger, options.MaxConcurrency, options.BoundedCapacity);
        using var activity = VespaActivitySource.Instance.StartActivity(VespaActivitySource.FeedPipeline);
        using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(options.BoundedCapacity, options.BoundedCapacity);
        var pipelineToken = pipelineCts.Token;
        var ready = Channel.CreateUnbounded<MixedDocumentQueue>(new UnboundedChannelOptions
        {
            SingleReader = options.MaxConcurrency == 1,
            AllowSynchronousContinuations = false
        });
        var gate = new object();
        var documents = new Dictionary<FeedOperation.DocumentKey, MixedDocumentQueue>();
        var sourceCompleted = false;
        var accumulator = new FeedResultAccumulator(_options.FeedMaxRetainedErrors);
        var processedCount = 0;
        var stopwatch = Stopwatch.StartNew();

        // Only one ready/active worker owns a document queue. Slots count pending
        // operations globally, including those queued behind an active document.
        // Thus both the per-document queues and the ready channel stay bounded.
        async Task ProduceAsync()
        {
            var count = 0;
            try
            {
                await foreach (var operation in operations.WithCancellation(pipelineToken))
                {
                    ArgumentNullException.ThrowIfNull(operation);
                    var key = operation.GetDocumentKey(_options.DefaultNamespace);
                    await slots.WaitAsync(pipelineToken);
                    lock (gate)
                    {
                        if (documents.TryGetValue(key, out var queue))
                            queue.Pending.Enqueue(new(operation, count));
                        else
                        {
                            queue = new(key);
                            queue.Pending.Enqueue(new(operation, count));
                            documents.Add(key, queue);
                            ready.Writer.TryWrite(queue);
                        }
                    }
                    count++;
                }
            }
            finally
            {
                accumulator.SetTotalDocuments(count);
                lock (gate)
                {
                    sourceCompleted = true;
                    if (documents.Count == 0)
                        ready.Writer.TryComplete();
                }
            }
        }

        async Task<VespaResponse> ExecuteWithRetryAsync(FeedOperation operation)
        {
            for (var attempt = 0; ; attempt++)
            {
                pipelineToken.ThrowIfCancellationRequested();
                try
                {
                    return await operation.ExecuteAsync(_documentOperations, _options.DefaultNamespace, pipelineToken);
                }
                catch (VespaException ex) when (
                    _options.EnableRetry && attempt < _options.MaxRetryAttempts && IsRetryableFeedError(ex))
                {
                    var delay = ComputeRetryDelay(_options.FeedRetryBaseDelay, attempt,
                        (ex as VespaTooManyRequestsException)?.RetryAfter);
                    if (_logger is not null)
                        LogDocumentFeedRetry(_logger, operation.Id, attempt + 1, delay.TotalMilliseconds, ex);
                    await Task.Delay(delay, pipelineToken);
                }
            }
        }

        async Task ConsumeAsync()
        {
            try
            {
                await foreach (var queue in ready.Reader.ReadAllAsync(pipelineToken))
                {
                    MixedFeedItem item;
                    lock (gate)
                        item = queue.Pending.Dequeue();
                    slots.Release();
                    var operation = item.Operation;
                    var ns = operation.Namespace ?? _options.DefaultNamespace;
                    try
                    {
                        var response = await ExecuteWithRetryAsync(operation);
                        if (response.HadIgnoredFields && accumulator.MarkIgnoredFields() && _logger is not null)
                            LogIgnoredFields(_logger, operation.Id);
                        accumulator.IncrementSuccess();
                    }
                    catch (OperationCanceledException) when (pipelineToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
                    {
                        accumulator.IncrementFailure();
                        accumulator.AddError(new FeedError
                        {
                            DocumentId = operation.Id,
                            Message = ex.Message,
                            StatusCode = (ex as VespaException)?.StatusCode,
                            OperationKind = operation.Kind,
                            DocumentType = operation.DocumentType,
                            Namespace = ns,
                            OperationIndex = item.Index
                        });
                        if (_logger is not null)
                            LogMixedOperationFailed(_logger, operation.Kind, operation.Id, operation.DocumentType, ns, ex);
                    }

                    var processed = Interlocked.Increment(ref processedCount);
                    options.OnProgress?.Invoke(new(processed, accumulator.SuccessCount, accumulator.FailureCount, operation.Id)
                    {
                        LastOperationKind = operation.Kind,
                        LastDocumentType = operation.DocumentType,
                        LastNamespace = ns,
                        OperationIndex = item.Index
                    });

                    lock (gate)
                    {
                        // Requeue one operation at a time so a hot document cannot
                        // starve other ready documents, even with one worker.
                        if (queue.Pending.Count > 0)
                            ready.Writer.TryWrite(queue);
                        else
                            documents.Remove(queue.Key);

                        if (sourceCompleted && documents.Count == 0)
                            ready.Writer.TryComplete();
                    }
                }
            }
            catch
            {
                pipelineCts.Cancel();
                throw;
            }
        }

        var tasks = new List<Task> { ProduceAsync() };
        for (var i = 0; i < options.MaxConcurrency; i++)
            tasks.Add(ConsumeAsync());

        try
        {
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
        var result = accumulator.ToResult(stopwatch.Elapsed);
        activity?.SetTag(VespaActivitySource.TagFeedCount, result.TotalOperations);
        activity?.SetTag(VespaActivitySource.TagFeedSuccess, result.SuccessCount);
        if (_logger is not null)
            LogPipelineComplete(_logger, result.SuccessCount, result.TotalOperations, stopwatch.ElapsedMilliseconds);
        return result;
    }

    private sealed class MixedDocumentQueue(FeedOperation.DocumentKey key)
    {
        internal FeedOperation.DocumentKey Key { get; } = key;
        internal Queue<MixedFeedItem> Pending { get; } = new();
    }

    private readonly record struct MixedFeedItem(FeedOperation Operation, int Index);

    [LoggerMessage(218, LogLevel.Error, "Failed feed operation {Kind} for {DocumentId} [{DocType}, {Namespace}]")]
    static partial void LogMixedOperationFailed(ILogger logger, FeedOperationKind kind, string documentId, string docType, string @namespace, Exception exception);
}
