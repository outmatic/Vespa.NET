using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Vespa.Documents;
using Vespa.Models;

namespace Vespa.Feed;

/// <summary>
/// Implementation of bulk feed operations
/// </summary>
public sealed partial class FeedOperations(
    IDocumentOperations documentOperations,
    VespaClientOptions options,
    ILogger? logger = null) : IFeedOperations
{
    private readonly IDocumentOperations _documentOperations =
        documentOperations ?? throw new ArgumentNullException(nameof(documentOperations));
    private readonly VespaClientOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger? _logger = logger;

    public Task<FeedResult> BulkPutAsync<T>(
        IEnumerable<FeedDocument<T>> documents,
        string documentType,
        string? @namespace = null,
        int maxConcurrency = 10,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);

        return ExecutePutPipelineAsync(
            documents.ToAsyncEnumerable(cancellationToken), documentType, @namespace,
            maxConcurrency, ComputeDefaultCapacity(maxConcurrency),
            onProgress: null, cancellationToken);
    }

    public Task<FeedResult> BulkUpdateAsync(
        IEnumerable<BulkFieldUpdate> updates,
        string documentType,
        string? @namespace = null,
        bool createIfMissing = false,
        int maxConcurrency = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);

        var feedDocs = updates.Select(u => new FeedDocument<Dictionary<string, FieldOperation>>
        {
            Id = u.Id,
            Fields = u.FieldOperations,
            Condition = u.Condition
        });

        return ExecutePipelineAsync(
            feedDocs.ToAsyncEnumerable(cancellationToken), documentType, @namespace,
            maxConcurrency, ComputeDefaultCapacity(maxConcurrency),
            (doc, ct) => _documentOperations.UpdateFieldsAsync(
                doc.Id, doc.Fields, documentType, @namespace,
                createIfMissing: createIfMissing,
                condition: doc.Condition,
                cancellationToken: ct),
            (docId, ex) => { if (_logger != null) LogDocumentUpdateFailed(_logger, docId, ex); },
            onProgress: null, cancellationToken);
    }

    public Task<FeedResult> BulkDeleteAsync(
        IEnumerable<string> documentIds,
        string documentType,
        string? @namespace = null,
        int maxConcurrency = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);

        var docs = documentIds.Select(id => new FeedDocument<object> { Id = id, Fields = null! });

        return ExecutePipelineAsync(
            docs.ToAsyncEnumerable(cancellationToken), documentType, @namespace,
            maxConcurrency, ComputeDefaultCapacity(maxConcurrency),
            async (doc, ct) =>
            {
                var response = await _documentOperations.DeleteAsync(
                    doc.Id, documentType, @namespace, cancellationToken: ct);
                if (_logger != null) LogDocumentDeleted(_logger, doc.Id);
                return response;
            },
            (docId, ex) => { if (_logger != null) LogDocumentDeleteFailed(_logger, docId, ex); },
            onProgress: null, cancellationToken);
    }

    public Task<FeedResult> FeedAsync<T>(
        IAsyncEnumerable<FeedDocument<T>> documents,
        string documentType,
        string? @namespace = null,
        int maxConcurrency = 64,
        int boundedCapacity = 256,
        Action<FeedProgress>? onProgress = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boundedCapacity);

        return ExecutePutPipelineAsync(
            documents, documentType, @namespace,
            maxConcurrency, boundedCapacity, onProgress, cancellationToken);
    }

    private Task<FeedResult> ExecutePutPipelineAsync<T>(
        IAsyncEnumerable<FeedDocument<T>> documents,
        string documentType,
        string? @namespace,
        int maxConcurrency,
        int boundedCapacity,
        Action<FeedProgress>? onProgress,
        CancellationToken cancellationToken) where T : class
    {
        return ExecutePipelineAsync(
            documents, documentType, @namespace,
            maxConcurrency, boundedCapacity,
            async (doc, ct) =>
            {
                var response = await _documentOperations.PutAsync(
                    doc.Id, doc.Fields, documentType, @namespace,
                    condition: doc.Condition, cancellationToken: ct);
                if (_logger != null) LogDocumentFed(_logger, doc.Id, documentType);
                return response;
            },
            (docId, ex) => { if (_logger != null) LogDocumentFeedFailed(_logger, docId, ex); },
            onProgress, cancellationToken);
    }

    /// <summary>
    /// Default channel capacity for the bulk wrappers: twice the consumer count,
    /// clamped so the multiplication cannot overflow.
    /// </summary>
    internal static int ComputeDefaultCapacity(int maxConcurrency) =>
        (int)Math.Min(2L * maxConcurrency, int.MaxValue);

    // ── Core pipeline engine ─────────────────────────────────────────────────

    private async Task<FeedResult> ExecutePipelineAsync<T>(
        IAsyncEnumerable<FeedDocument<T>> documents,
        string documentType,
        string? @namespace,
        int maxConcurrency,
        int boundedCapacity,
        Func<FeedDocument<T>, CancellationToken, Task<VespaResponse>> operation,
        Action<string, Exception> onError,
        Action<FeedProgress>? onProgress,
        CancellationToken cancellationToken) where T : class
    {
        if (_logger != null) LogPipelineStart(_logger, maxConcurrency, boundedCapacity);

        using var activity = VespaActivitySource.Instance.StartActivity(VespaActivitySource.FeedPipeline);
        activity?.SetTag(VespaActivitySource.TagDocType, documentType);
        activity?.SetTag(VespaActivitySource.TagNamespace, @namespace ?? _options.DefaultNamespace);

        var channel = Channel.CreateBounded<FeedDocument<T>>(
            new BoundedChannelOptions(boundedCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true
            });

        var accumulator = new FeedResultAccumulator(_options.FeedMaxRetainedErrors);
        var processedCount = 0;
        var stopwatch = Stopwatch.StartNew();

        // A consumer dying (e.g. a throwing onProgress callback) must unblock the
        // producer's WriteAsync on the bounded channel, or the pipeline deadlocks.
        using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pipelineToken = pipelineCts.Token;

        // Producer: reads from IAsyncEnumerable, writes to bounded channel.
        // On a source-enumeration fault the writer is still completed (not cancelled):
        // already-buffered documents are drained by the consumers, and their outcomes
        // are visible in the partial result carried by the VespaFeedException.
        async Task ProduceAsync()
        {
            var count = 0;
            try
            {
                await foreach (var doc in documents.WithCancellation(pipelineToken))
                {
                    await channel.Writer.WriteAsync(doc, pipelineToken);
                    count++;
                }
            }
            finally
            {
                accumulator.SetTotalDocuments(count);
                channel.Writer.Complete();
            }
        }

        // Consumer: reads from channel and executes operation
        async Task ConsumeAsync()
        {
            try
            {
                await foreach (var doc in channel.Reader.ReadAllAsync(pipelineToken))
                {
                    try
                    {
                        var response = await ExecuteWithRetryAsync(doc, doc.Id, operation, pipelineToken);
                        if (response.HadIgnoredFields && accumulator.MarkIgnoredFields() && _logger != null)
                            LogIgnoredFields(_logger, doc.Id);
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
                            DocumentId = doc.Id,
                            Message = ex.Message,
                            StatusCode = (ex as VespaException)?.StatusCode
                        });
                        onError(doc.Id, ex);
                    }

                    // Single shared counter incremented once per outcome; success/failure are
                    // read *after* the increment so SuccessCount + FailureCount >= ProcessedCount
                    // holds within every snapshot and ProcessedCount is monotonic.
                    var processed = Interlocked.Increment(ref processedCount);
                    onProgress?.Invoke(new FeedProgress(processed, accumulator.SuccessCount, accumulator.FailureCount, doc.Id));
                }
            }
            catch
            {
                pipelineCts.Cancel();
                throw;
            }
        }

        var producerTask = ProduceAsync();
        var allTasks = new List<Task>(maxConcurrency + 1) { producerTask };
        allTasks.AddRange(Enumerable.Range(0, maxConcurrency).Select(_ => ConsumeAsync()));

        var result = await CompletePipelineAsync(allTasks, accumulator, stopwatch, cancellationToken);
        activity?.SetTag(VespaActivitySource.TagFeedCount, result.TotalDocuments);
        activity?.SetTag(VespaActivitySource.TagFeedSuccess, result.SuccessCount);

        if (_logger != null)
            LogPipelineComplete(_logger, result.SuccessCount, result.TotalDocuments, stopwatch.ElapsedMilliseconds);

        return result;
    }

    [LoggerMessage(203, LogLevel.Debug, "Fed document {DocumentId} [{DocType}]")]
    static partial void LogDocumentFed(ILogger logger, string documentId, string docType);

    [LoggerMessage(204, LogLevel.Error, "Failed to feed document {DocumentId}")]
    static partial void LogDocumentFeedFailed(ILogger logger, string documentId, Exception ex);

    [LoggerMessage(208, LogLevel.Debug, "Deleted document {DocumentId}")]
    static partial void LogDocumentDeleted(ILogger logger, string documentId);

    [LoggerMessage(209, LogLevel.Error, "Failed to delete document {DocumentId}")]
    static partial void LogDocumentDeleteFailed(ILogger logger, string documentId, Exception ex);

    [LoggerMessage(212, LogLevel.Error, "Failed to update document {DocumentId}")]
    static partial void LogDocumentUpdateFailed(ILogger logger, string documentId, Exception ex);

    [LoggerMessage(214, LogLevel.Information, "Starting feed pipeline (concurrency={Concurrency}, buffer={BufferSize})")]
    static partial void LogPipelineStart(ILogger logger, int concurrency, int bufferSize);

    [LoggerMessage(215, LogLevel.Information, "Feed pipeline complete: {Success}/{Total} in {ElapsedMs}ms")]
    static partial void LogPipelineComplete(ILogger logger, int success, int total, long elapsedMs);

    [LoggerMessage(216, LogLevel.Warning,
        "Retrying document {DocumentId} after transient failure (attempt {Attempt}, delay {DelayMs}ms)")]
    static partial void LogDocumentFeedRetry(ILogger logger, string documentId, int attempt, double delayMs, Exception ex);

    [LoggerMessage(217, LogLevel.Warning,
        "Vespa ignored unknown fields during this feed (first seen on document {DocumentId}) — check for a document/schema mismatch")]
    static partial void LogIgnoredFields(ILogger logger, string documentId);
}
