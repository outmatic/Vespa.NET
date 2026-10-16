using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Vespa.Models;

namespace Vespa.Documents;

/// <summary>
/// Implementation of document CRUD, selection and visit operations
/// </summary>
public sealed partial class DocumentOperations(
    HttpClient httpClient,
    VespaClientOptions options,
    ILogger? logger = null) : IDocumentOperations
{
    private const string QueryParamCluster = "cluster";
    private const string QueryParamCondition = "condition";
    private const string QueryParamContinuation = "continuation";
    private const string QueryParamCreate = "create";
    private const string QueryParamFieldSet = "fieldSet";
    private const string QueryParamSelection = "selection";
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly VespaClientOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<VespaResponse> PutAsync<T>(
        string documentId,
        T document,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        bool create = false,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentNullException.ThrowIfNull(document);

        var (ns, url) = BuildDocIdUrl(documentType, documentId, @namespace, requestOptions,
            (QueryParamCondition, condition),
            (QueryParamCreate, create ? "true" : null));

        if (logger != null)
            LogPuttingDocument(logger, documentId, documentType);

        using var activity = StartDocumentActivity(VespaActivitySource.DocumentPut, documentType, ns, documentId);

        return await SendPutAsync(url, document, cancellationToken);
    }

    public async Task<VespaDocument<T>?> GetAsync<T>(
        string documentId,
        string documentType,
        string? @namespace = null,
        string? fieldSet = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);

        var (ns, url) = BuildDocIdUrl(documentType, documentId, @namespace, requestOptions,
            (QueryParamFieldSet, fieldSet),
            (QueryParamCluster, requestOptions?.Cluster));

        if (logger != null)
            LogGettingDocument(logger, documentId, documentType);

        using var activity = StartDocumentActivity(VespaActivitySource.DocumentGet, documentType, ns, documentId);

        return await GetDocumentOrNullAsync<T>(url, documentId, cancellationToken);
    }

    public async Task<VespaResponse> DeleteAsync(
        string documentId,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);

        var (ns, url) = BuildDocIdUrl(documentType, documentId, @namespace, requestOptions,
            (QueryParamCondition, condition));

        if (logger != null)
            LogDeletingDocument(logger, documentId, documentType);

        using var activity = StartDocumentActivity(VespaActivitySource.DocumentDelete, documentType, ns, documentId);

        return await SendDeleteAsync(url, cancellationToken);
    }

    public async Task<VespaResponse> UpdateFieldsAsync(
        string documentId,
        Dictionary<string, FieldOperation> fieldOperations,
        string documentType,
        string? @namespace = null,
        bool createIfMissing = false,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentNullException.ThrowIfNull(fieldOperations);

        var (ns, url) = BuildDocIdUrl(documentType, documentId, @namespace, requestOptions,
            (QueryParamCreate, createIfMissing ? "true" : null),
            (QueryParamCondition, condition));

        if (logger != null)
            LogUpdatingDocument(logger, documentId, documentType);

        using var activity = StartDocumentActivity(VespaActivitySource.DocumentUpdate, documentType, ns, documentId);

        return await SendUpdateAsync(url, fieldOperations, cancellationToken);
    }

    private Activity? StartDocumentActivity(
        string operationName, string documentType, string ns, string? documentId = null)
    {
        var activity = VespaActivitySource.Instance.StartActivity(operationName);
        activity?.SetTag(VespaActivitySource.TagDocType, documentType);
        activity?.SetTag(VespaActivitySource.TagNamespace, ns);
        // Document ids can embed user identifiers — only export them to tracing
        // backends when explicitly opted in (RecordQueryTextInTraces).
        if (documentId is not null && _options.RecordQueryTextInTraces)
            activity?.SetTag(VespaActivitySource.TagDocId, documentId);
        return activity;
    }

    [LoggerMessage(EventId = 101, Level = LogLevel.Debug, Message = "Putting document {DocumentId} of type {DocumentType}")]
    static partial void LogPuttingDocument(ILogger logger, string documentId, string documentType);

    [LoggerMessage(EventId = 102, Level = LogLevel.Debug, Message = "Getting document {DocumentId} of type {DocumentType}")]
    static partial void LogGettingDocument(ILogger logger, string documentId, string documentType);

    [LoggerMessage(EventId = 103, Level = LogLevel.Debug, Message = "Document {DocumentId} not found")]
    static partial void LogDocumentNotFound(ILogger logger, string documentId);

    [LoggerMessage(EventId = 104, Level = LogLevel.Debug, Message = "Deleting document {DocumentId} of type {DocumentType}")]
    static partial void LogDeletingDocument(ILogger logger, string documentId, string documentType);

    [LoggerMessage(EventId = 105, Level = LogLevel.Debug, Message = "Updating document {DocumentId} of type {DocumentType}")]
    static partial void LogUpdatingDocument(ILogger logger, string documentId, string documentType);
}
