using System.Diagnostics;
using System.Runtime.CompilerServices;
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

        return FeedAsync(
            documents.ToAsyncEnumerable(cancellationToken), documentType, @namespace,
            maxConcurrency, boundedCapacity: maxConcurrency * 2,
            onProgress: null, cancellationToken: cancellationToken);
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

        var feedDocs = updates.Select(u => new FeedDocument<Dictionary<string, FieldOperation>>
        {
            Id = u.Id,
            Fields = u.FieldOperations,
            Condition = u.Condition
        });

        return ExecutePipelineAsync(
            feedDocs.ToAsyncEnumerable(cancellationToken), documentType, @namespace,
            maxConcurrency, maxConcurrency * 2,
            async (doc, ct) =>
            {
                await _documentOperations.UpdateFieldsAsync(
                    doc.Id, doc.Fields, documentType, @namespace,
                    createIfMissing: createIfMissing,
                    condition: doc.Condition,
                    cancellationToken: ct);
            },
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

        var docs = documentIds.Select(id => new FeedDocument<object> { Id = id, Fields = null! });

        return ExecutePipelineAsync(
            docs.ToAsyncEnumerable(cancellationToken), documentType, @namespace,
            maxConcurrency, maxConcurrency * 2,
            async (doc, ct) =>
            {
                await _documentOperations.DeleteAsync(
                    doc.Id, documentType, @namespace, cancellationToken: ct);
                if (_logger != null) LogDocumentDeleted(_logger, doc.Id);
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

        return ExecutePipelineAsync(
            documents, documentType, @namespace,
            maxConcurrency, boundedCapacity,
            async (doc, ct) =>
            {
                await _documentOperations.PutAsync(
                    doc.Id, doc.Fields, documentType, @namespace,
                    condition: doc.Condition, cancellationToken: ct);
                if (_logger != null) LogDocumentFed(_logger, doc.Id, documentType);
            },
            (docId, ex) => { if (_logger != null) LogDocumentFeedFailed(_logger, docId, ex); },
            onProgress, cancellationToken);
    }

    // ── Core pipeline engine ─────────────────────────────────────────────────

    private async Task<FeedResult> ExecutePipelineAsync<T>(
        IAsyncEnumerable<FeedDocument<T>> documents,
        string documentType,
        string? @namespace,
        int maxConcurrency,
        int boundedCapacity,
        Func<FeedDocument<T>, CancellationToken, Task> operation,
        Action<string, Exception> onError,
        Action<FeedProgress>? onProgress,
        CancellationToken cancellationToken) where T : class
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boundedCapacity);

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

        var result = new FeedResult();
        var stopwatch = Stopwatch.StartNew();

        // Producer: reads from IAsyncEnumerable, writes to bounded channel
        async Task ProduceAsync()
        {
            var count = 0;
            try
            {
                await foreach (var doc in documents.WithCancellation(cancellationToken))
                {
                    await channel.Writer.WriteAsync(doc, cancellationToken);
                    count++;
                }
            }
            finally
            {
                result.TotalDocuments = count;
                channel.Writer.Complete();
            }
        }

        // Consumer: reads from channel and executes operation
        async Task ConsumeAsync()
        {
            await foreach (var doc in channel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await operation(doc, cancellationToken);
                    result.IncrementSuccess();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
                {
                    result.IncrementFailure();
                    result.Errors.Enqueue(new FeedError
                    {
                        DocumentId = doc.Id,
                        Message = ex.Message,
                        StatusCode = (ex as VespaException)?.StatusCode
                    });
                    onError(doc.Id, ex);
                }

                onProgress?.Invoke(new FeedProgress(result.SuccessCount, result.FailureCount, doc.Id));
            }
        }

        var producerTask = ProduceAsync();
        var consumers = Enumerable.Range(0, maxConcurrency).Select(_ => ConsumeAsync()).ToArray();

        await producerTask;
        await Task.WhenAll(consumers);

        stopwatch.Stop();
        result.Duration = stopwatch.Elapsed;
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
}

/// <summary>
/// Extension to bridge <see cref="IEnumerable{T}"/> to <see cref="IAsyncEnumerable{T}"/>.
/// </summary>
file static class EnumerableExtensions
{
#pragma warning disable CS1998 // Async method lacks 'await' operators
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(
        this IEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
#pragma warning restore CS1998
    {
        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }
}
