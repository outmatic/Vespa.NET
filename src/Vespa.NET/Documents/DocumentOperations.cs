using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vespa.Models;

namespace Vespa.Documents;

/// <summary>
/// Implementation of document CRUD operations
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
    private const string QueryParamWantedDocumentCount = "wantedDocumentCount";
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly VespaClientOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<VespaResponse> PutAsync<T>(
        string documentId,
        T document,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentNullException.ThrowIfNull(document);

        var (ns, url) = BuildDocIdUrl(documentType, documentId, @namespace, requestOptions,
            (QueryParamCondition, condition));

        if (logger != null)
            LogPuttingDocument(logger, documentId, documentType);

        using var activity = StartDocumentActivity(VespaActivitySource.DocumentPut, documentType, ns, documentId);

        var payload = new { fields = document };
        using var response = await _httpClient.PostAsJsonAsync(url, payload, VespaJsonOptions.Default, cancellationToken);

        return await ParseResponseAsync(response, cancellationToken);
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
            (QueryParamFieldSet, fieldSet));

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

        using var response = await _httpClient.DeleteAsync(url, cancellationToken);

        return await ParseResponseAsync(response, cancellationToken);
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

        var payload = new { fields = fieldOperations };
        using var response = await _httpClient.PutAsJsonAsync(url, payload, VespaJsonOptions.Default, cancellationToken);

        return await ParseResponseAsync(response, cancellationToken);
    }

    public Task<VespaDocument<T>?> GetByGroupAsync<T>(
        string group,
        string localId,
        string documentType,
        string? @namespace = null,
        string? fieldSet = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
        => GetByAddressingAsync<T>("group", Uri.EscapeDataString(group), localId, documentType, @namespace, fieldSet, requestOptions, cancellationToken);

    public Task<VespaDocument<T>?> GetByNumberAsync<T>(
        long number,
        string localId,
        string documentType,
        string? @namespace = null,
        string? fieldSet = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
        => GetByAddressingAsync<T>("number", number.ToString(), localId, documentType, @namespace, fieldSet, requestOptions, cancellationToken);

    public Task<VespaResponse> PutByGroupAsync<T>(
        string group,
        string localId,
        T document,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
        => PutByAddressingAsync("group", Uri.EscapeDataString(group), localId, document, documentType, @namespace, condition, requestOptions, cancellationToken);

    public Task<VespaResponse> PutByNumberAsync<T>(
        long number,
        string localId,
        T document,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
        => PutByAddressingAsync("number", number.ToString(), localId, document, documentType, @namespace, condition, requestOptions, cancellationToken);

    public Task<VespaResponse> UpdateFieldsByGroupAsync(
        string group,
        string localId,
        Dictionary<string, FieldOperation> fieldOperations,
        string documentType,
        string? @namespace = null,
        bool createIfMissing = false,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => UpdateFieldsByAddressingAsync("group", Uri.EscapeDataString(group), localId, fieldOperations, documentType, @namespace, createIfMissing, condition, requestOptions, cancellationToken);

    public Task<VespaResponse> UpdateFieldsByNumberAsync(
        long number,
        string localId,
        Dictionary<string, FieldOperation> fieldOperations,
        string documentType,
        string? @namespace = null,
        bool createIfMissing = false,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => UpdateFieldsByAddressingAsync("number", number.ToString(), localId, fieldOperations, documentType, @namespace, createIfMissing, condition, requestOptions, cancellationToken);

    public Task<VespaResponse> DeleteByGroupAsync(
        string group,
        string localId,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => DeleteByAddressingAsync("group", Uri.EscapeDataString(group), localId, documentType, @namespace, condition, requestOptions, cancellationToken);

    public Task<VespaResponse> DeleteByNumberAsync(
        long number,
        string localId,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => DeleteByAddressingAsync("number", number.ToString(), localId, documentType, @namespace, condition, requestOptions, cancellationToken);

    private async Task<VespaDocument<T>?> GetByAddressingAsync<T>(
        string scheme,
        string bucket,
        string localId,
        string documentType,
        string? @namespace,
        string? fieldSet,
        DocumentRequestOptions? requestOptions,
        CancellationToken cancellationToken) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);

        var (ns, url) = BuildAddressingUrl(scheme, bucket, localId, documentType, @namespace, requestOptions,
            (QueryParamFieldSet, fieldSet));

        using var activity = StartDocumentActivity(VespaActivitySource.DocumentGet, documentType, ns);
        return await GetDocumentOrNullAsync<T>(url, null, cancellationToken);
    }

    private async Task<VespaResponse> PutByAddressingAsync<T>(
        string scheme,
        string bucket,
        string localId,
        T document,
        string documentType,
        string? @namespace,
        string? condition,
        DocumentRequestOptions? requestOptions,
        CancellationToken cancellationToken) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentNullException.ThrowIfNull(document);

        var (ns, url) = BuildAddressingUrl(scheme, bucket, localId, documentType, @namespace, requestOptions,
            (QueryParamCondition, condition));

        using var activity = StartDocumentActivity(VespaActivitySource.DocumentPut, documentType, ns);

        var payload = new { fields = document };
        using var response = await _httpClient.PostAsJsonAsync(url, payload, VespaJsonOptions.Default, cancellationToken);

        return await ParseResponseAsync(response, cancellationToken);
    }

    private async Task<VespaResponse> UpdateFieldsByAddressingAsync(
        string scheme,
        string bucket,
        string localId,
        Dictionary<string, FieldOperation> fieldOperations,
        string documentType,
        string? @namespace,
        bool createIfMissing,
        string? condition,
        DocumentRequestOptions? requestOptions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentNullException.ThrowIfNull(fieldOperations);

        var (ns, url) = BuildAddressingUrl(scheme, bucket, localId, documentType, @namespace, requestOptions,
            (QueryParamCreate, createIfMissing ? "true" : null),
            (QueryParamCondition, condition));

        using var activity = StartDocumentActivity(VespaActivitySource.DocumentUpdate, documentType, ns);

        var payload = new { fields = fieldOperations };
        using var response = await _httpClient.PutAsJsonAsync(url, payload, VespaJsonOptions.Default, cancellationToken);

        return await ParseResponseAsync(response, cancellationToken);
    }

    private async Task<VespaResponse> DeleteByAddressingAsync(
        string scheme,
        string bucket,
        string localId,
        string documentType,
        string? @namespace,
        string? condition,
        DocumentRequestOptions? requestOptions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);

        var (ns, url) = BuildAddressingUrl(scheme, bucket, localId, documentType, @namespace, requestOptions,
            (QueryParamCondition, condition));

        using var activity = StartDocumentActivity(VespaActivitySource.DocumentDelete, documentType, ns);

        using var response = await _httpClient.DeleteAsync(url, cancellationToken);

        return await ParseResponseAsync(response, cancellationToken);
    }

    private (string Namespace, string Url) BuildAddressingUrl(
        string scheme,
        string bucket,
        string localId,
        string documentType,
        string? @namespace,
        DocumentRequestOptions? requestOptions,
        params (string Key, string? Value)[] extraParams)
    {
        var ns = ResolveNamespace(@namespace);
        var url = BuildUrl(
            $"{VespaPaths.DocV1}/{ns}/{documentType}/{scheme}/{bucket}/{Uri.EscapeDataString(localId)}",
            requestOptions,
            extraParams);
        return (ns, url);
    }

    public Task<VespaResponse> UpdateBySelectionAsync(
        string selection,
        Dictionary<string, FieldOperation> fieldOperations,
        string documentType,
        string? @namespace = null,
        string? cluster = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentNullException.ThrowIfNull(fieldOperations);

        // The factory must build a fresh content per chunk: each HttpRequestMessage
        // disposes its content, and the continuation loop sends one request per chunk.
        return ExecuteSelectionOperationAsync(
            HttpMethod.Put, selection, documentType, @namespace, cluster,
            extraParams: [],
            requestOptions: requestOptions,
            contentFactory: () => JsonContent.Create(new { fields = fieldOperations }, options: VespaJsonOptions.Default),
            cancellationToken);
    }

    public Task<VespaResponse> DeleteBySelectionAsync(
        string selection,
        string documentType,
        string? @namespace = null,
        string? cluster = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);

        return ExecuteSelectionOperationAsync(
            HttpMethod.Delete, selection, documentType, @namespace, cluster,
            extraParams: [],
            requestOptions: requestOptions,
            contentFactory: null,
            cancellationToken);
    }

    public Task<VespaResponse> CopyBySelectionAsync(
        string selection,
        string documentType,
        string cluster,
        string destinationCluster,
        string? @namespace = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(cluster);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationCluster);
        ThrowIfStreamRequestedOnCopy(requestOptions);

        return ExecuteSelectionOperationAsync(
            HttpMethod.Post, selection, documentType, @namespace, cluster,
            extraParams: [("destinationCluster", destinationCluster)],
            requestOptions: requestOptions,
            contentFactory: null,
            cancellationToken);
    }

    private static void ThrowIfStreamRequestedOnCopy(SelectionRequestOptions? requestOptions)
    {
        if (requestOptions?.Stream == true)
            throw new ArgumentException(
                "SelectionRequestOptions.Stream is not supported on CopyBySelection — Vespa does not accept stream=true on the copy endpoint.",
                nameof(requestOptions));
    }

    private async Task<VespaResponse> ExecuteSelectionOperationAsync(
        HttpMethod method,
        string selection,
        string documentType,
        string? @namespace,
        string? cluster,
        (string Key, string? Value)[] extraParams,
        SelectionRequestOptions? requestOptions,
        Func<HttpContent>? contentFactory,
        CancellationToken cancellationToken)
    {
        long totalDocumentCount = 0;
        SelectionPageResult page;
        string? continuation = null;

        do
        {
            // Cancellation propagates from the HTTP call/body read — exiting the loop
            // early would falsely report a partially-applied operation as successful.
            page = await ExecuteSelectionChunkAsync(
                method, selection, documentType, @namespace, cluster,
                extraParams, requestOptions, continuation, contentFactory, cancellationToken);
            totalDocumentCount += page.DocumentCount;
            continuation = page.Continuation;
        }
        while (continuation is not null);

        return new VespaResponse
        {
            IsSuccess = true,
            StatusCode = page.StatusCode,
            Message = "Selection operation completed successfully",
            DocumentCount = totalDocumentCount,
            IgnoredFields = page.IgnoredFields
        };
    }

    private async Task<SelectionPageResult> ExecuteSelectionChunkAsync(
        HttpMethod method,
        string selection,
        string documentType,
        string? @namespace,
        string? cluster,
        (string Key, string? Value)[] extraParams,
        SelectionRequestOptions? requestOptions,
        string? continuation,
        Func<HttpContent>? contentFactory,
        CancellationToken cancellationToken)
    {
        var streaming = requestOptions?.Stream == true;
        var optionParams = requestOptions?.ToQueryParams().ToArray() ?? [];
        var queryParams = new List<(string Key, string? Value)>(extraParams.Length + optionParams.Length + 4)
        {
            (QueryParamSelection, selection),
            (QueryParamCluster, cluster),
            (QueryParamContinuation, continuation)
        };
        if (streaming)
            queryParams.Add(("stream", "true"));
        queryParams.AddRange(extraParams);
        queryParams.AddRange(optionParams);

        var url = BuildDocBasePath(documentType, @namespace, null, [.. queryParams]);

        using var request = new HttpRequestMessage(method, url);
        if (streaming)
        {
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/jsonl"));
        }
        if (contentFactory is not null)
            request.Content = contentFactory();

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            await ThrowFromErrorResponseAsync(response, cancellationToken);
            return null!; // unreachable
        }

        IReadOnlyList<string>? ignoredFields = null;
        if (response.Headers.TryGetValues("X-Vespa-Ignored-Fields", out var headerValues))
        {
            ignoredFields = headerValues
                .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();
        }

        if (streaming)
        {
            var streamed = await ReadStreamingSelectionBodyAsync(response, cancellationToken);
            return new SelectionPageResult
            {
                DocumentCount = streamed.DocumentCount,
                Continuation = streamed.Continuation,
                StatusCode = (int)response.StatusCode,
                IgnoredFields = ignoredFields
            };
        }

        var body = await TryReadSelectionBodyAsync(response, cancellationToken);
        return new SelectionPageResult
        {
            DocumentCount = body?.DocumentCount ?? 0,
            Continuation = body?.Continuation,
            StatusCode = (int)response.StatusCode,
            IgnoredFields = ignoredFields
        };
    }

    /// <summary>
    /// Reads a streaming JSONL selection response. Each line is a JSON object with
    /// one of these shapes:
    /// <list type="bullet">
    ///   <item><c>{"sessionStats": {"documentCount": N}}</c> — emitted once (or zero times); holds the chunk total.</item>
    ///   <item><c>{"continuation": {"token": "…", "percentFinished": N}}</c> — emitted per visited bucket; the last one wins.</item>
    ///   <item><c>{"message": {...}}</c>, and others — ignored.</item>
    /// </list>
    /// If the last continuation has no <c>token</c>, the operation is complete.
    /// </summary>
    private static async Task<(long DocumentCount, string? Continuation)> ReadStreamingSelectionBodyAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        long documentCount = 0;
        string? lastContinuationToken = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            System.Text.Json.JsonDocument doc;
            try { doc = System.Text.Json.JsonDocument.Parse(line); }
            catch (System.Text.Json.JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;

                if (root.TryGetProperty("sessionStats", out var stats) &&
                    stats.TryGetProperty("documentCount", out var dc) &&
                    dc.TryGetInt64(out var n))
                {
                    documentCount += n;
                }

                if (root.TryGetProperty("continuation", out var cont))
                {
                    lastContinuationToken = cont.TryGetProperty("token", out var tok) && tok.ValueKind == System.Text.Json.JsonValueKind.String
                        ? tok.GetString()
                        : null;
                }
            }
        }

        return (documentCount, lastContinuationToken);
    }

    public Task<SelectionPageResult> UpdateBySelectionPageAsync(
        string selection,
        Dictionary<string, FieldOperation> fieldOperations,
        string documentType,
        string? @namespace = null,
        string? cluster = null,
        string? continuation = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentNullException.ThrowIfNull(fieldOperations);

        return ExecuteSelectionChunkAsync(
            HttpMethod.Put, selection, documentType, @namespace, cluster,
            extraParams: [],
            requestOptions: requestOptions,
            continuation: continuation,
            contentFactory: () => JsonContent.Create(new { fields = fieldOperations }, options: VespaJsonOptions.Default),
            cancellationToken);
    }

    public Task<SelectionPageResult> DeleteBySelectionPageAsync(
        string selection,
        string documentType,
        string? @namespace = null,
        string? cluster = null,
        string? continuation = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);

        return ExecuteSelectionChunkAsync(
            HttpMethod.Delete, selection, documentType, @namespace, cluster,
            extraParams: [],
            requestOptions: requestOptions,
            continuation: continuation,
            contentFactory: null,
            cancellationToken);
    }

    public Task<SelectionPageResult> CopyBySelectionPageAsync(
        string selection,
        string documentType,
        string cluster,
        string destinationCluster,
        string? @namespace = null,
        string? continuation = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(cluster);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationCluster);
        ThrowIfStreamRequestedOnCopy(requestOptions);

        return ExecuteSelectionChunkAsync(
            HttpMethod.Post, selection, documentType, @namespace, cluster,
            extraParams: [("destinationCluster", destinationCluster)],
            requestOptions: requestOptions,
            continuation: continuation,
            contentFactory: null,
            cancellationToken);
    }

    private static async Task<SelectionResponseBody?> TryReadSelectionBodyAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<SelectionResponseBody>(VespaJsonOptions.Default, cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (System.IO.IOException)
        {
            return null;
        }
    }

    private static async Task ThrowFromErrorResponseAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        VespaError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<VespaError>(VespaJsonOptions.Default, cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            // fall through
        }

        var message = error?.Message ?? $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}";
        throw VespaException.FromStatusCode((int)response.StatusCode, message, error);
    }

    private sealed record SelectionResponseBody(
        [property: System.Text.Json.Serialization.JsonPropertyName("documentCount")] long? DocumentCount,
        [property: System.Text.Json.Serialization.JsonPropertyName("continuation")] string? Continuation,
        [property: System.Text.Json.Serialization.JsonPropertyName("message")] string? Message);

    public async IAsyncEnumerable<VespaDocument<T>> VisitJsonlAsync<T>(
        string documentType,
        string? selection = null,
        string? cluster = null,
        string? @namespace = null,
        string? fieldSet = null,
        int? wantedDocumentCount = null,
        TimeSpan? timeout = null,
        int? slices = null,
        int? sliceId = null,
        int? concurrency = null,
        long? fromTimestamp = null,
        long? toTimestamp = null,
        bool? includeRemoves = null,
        string? bucketSpace = null,
        DocumentRequestOptions? requestOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);

        var url = BuildDocBasePath(documentType, @namespace, requestOptions,
            (QueryParamCluster, cluster),
            (QueryParamSelection, selection),
            (QueryParamFieldSet, fieldSet),
            (QueryParamWantedDocumentCount, wantedDocumentCount?.ToString()),
            ("timeout", timeout.HasValue ? $"{(int)timeout.Value.TotalMilliseconds}ms" : null),
            ("slices", slices?.ToString()),
            ("sliceId", sliceId?.ToString()),
            ("concurrency", concurrency?.ToString()),
            ("fromTimestamp", fromTimestamp?.ToString()),
            ("toTimestamp", toTimestamp?.ToString()),
            ("includeRemoves", includeRemoves.HasValue ? (includeRemoves.Value ? "true" : "false") : null),
            ("bucketSpace", bucketSpace),
            ("stream", "true"));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/jsonl"));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw VespaException.FromStatusCode((int)response.StatusCode, $"JSONL visit failed with status {response.StatusCode}");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var doc = ParseJsonlLine<T>(line);
            if (doc is not null)
                yield return doc;
        }
    }

    /// <summary>
    /// Parses one document/v1 JSONL stream line. Document lines are
    /// <c>{"put":"id:...","fields":{...}}</c>, tombstones (only with
    /// <c>includeRemoves=true</c>) are <c>{"remove":"id:..."}</c> and yield a document
    /// with <see langword="null"/> <c>Fields</c>; <c>{"continuation":{...}}</c> progress
    /// markers and other metadata return <see langword="null"/>.
    /// </summary>
    private static VespaDocument<T>? ParseJsonlLine<T>(string line) where T : class
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;

        if (root.TryGetProperty("remove", out var removeElem))
            return new VespaDocument<T>
            {
                Id = VespaDocumentId.GetUserSpecified(removeElem.GetString() ?? string.Empty)
            };

        string? id = null;
        if (root.TryGetProperty("put", out var putElem))
            id = putElem.GetString();
        else if (root.TryGetProperty("id", out var idElem))
            id = idElem.GetString();

        if (id is null)
            return null;

        var doc = new VespaDocument<T>
        {
            Id = VespaDocumentId.GetUserSpecified(id),
            Fields = root.TryGetProperty("fields", out var fieldsElem)
                ? fieldsElem.Deserialize<T>(VespaJsonOptions.Default)
                : null
        };
        VespaIdInjector.Inject(doc);
        return doc;
    }

    public async IAsyncEnumerable<VespaDocument<T>> VisitAsync<T>(
        string documentType,
        string? selection = null,
        string? cluster = null,
        string? @namespace = null,
        string? fieldSet = null,
        int? wantedDocumentCount = null,
        TimeSpan? timeout = null,
        int? slices = null,
        int? sliceId = null,
        int? concurrency = null,
        long? fromTimestamp = null,
        long? toTimestamp = null,
        bool? includeRemoves = null,
        string? bucketSpace = null,
        DocumentRequestOptions? requestOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);

        var basePath = DocBasePath(documentType, @namespace);
        string? continuation = null;

        do
        {
            var url = BuildUrl(basePath,
                requestOptions,
                (QueryParamCluster, cluster),
                (QueryParamSelection, selection),
                (QueryParamFieldSet, fieldSet),
                (QueryParamWantedDocumentCount, wantedDocumentCount?.ToString()),
                ("timeout", timeout.HasValue ? $"{(int)timeout.Value.TotalMilliseconds}ms" : null),
                ("slices", slices?.ToString()),
                ("sliceId", sliceId?.ToString()),
                ("concurrency", concurrency?.ToString()),
                ("fromTimestamp", fromTimestamp?.ToString()),
                ("toTimestamp", toTimestamp?.ToString()),
                ("includeRemoves", includeRemoves.HasValue ? (includeRemoves.Value ? "true" : "false") : null),
                ("bucketSpace", bucketSpace),
                (QueryParamContinuation, continuation));

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw VespaException.FromStatusCode((int)response.StatusCode, $"Visit failed with status {response.StatusCode}");

            var page = await VespaIdInjector.DeserializeVisitAndInjectAsync<T>(response.Content, cancellationToken);
            if (page is null)
                yield break;

            foreach (var doc in page.Documents)
                yield return doc;

            continuation = page.Continuation;
        }
        while (continuation is not null);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string ResolveNamespace(string? @namespace) =>
        @namespace ?? _options.DefaultNamespace;

    /// <summary>
    /// Strips the Vespa ID prefix (<c>id:{namespace}:{doctype}:{k/v}:</c>) when present,
    /// so callers can pass either the bare user ID or the full Vespa document ID.
    /// Bare IDs — including ones that happen to contain <c>::</c> — pass through unchanged.
    /// </summary>
    private static string NormalizeId(string id) => VespaDocumentId.GetUserSpecified(id);

    private string DocBasePath(string documentType, string? @namespace) =>
        $"{VespaPaths.DocV1}/{ResolveNamespace(@namespace)}/{documentType}/docid/";

    private (string Namespace, string Url) BuildDocIdUrl(
        string documentType,
        string documentId,
        string? @namespace,
        DocumentRequestOptions? requestOptions,
        params (string Key, string? Value)[] extraParams)
    {
        var ns = ResolveNamespace(@namespace);
        var url = BuildUrl(
            $"{VespaPaths.DocV1}/{ns}/{documentType}/docid/{Uri.EscapeDataString(NormalizeId(documentId))}",
            requestOptions,
            extraParams);
        return (ns, url);
    }

    private string BuildDocBasePath(
        string documentType,
        string? @namespace,
        DocumentRequestOptions? requestOptions,
        params (string Key, string? Value)[] extraParams) =>
        BuildUrl(DocBasePath(documentType, @namespace), requestOptions, extraParams);

    private static System.Diagnostics.Activity? StartDocumentActivity(
        string operationName, string documentType, string ns, string? documentId = null)
    {
        var activity = VespaActivitySource.Instance.StartActivity(operationName);
        activity?.SetTag(VespaActivitySource.TagDocType, documentType);
        activity?.SetTag(VespaActivitySource.TagNamespace, ns);
        if (documentId is not null)
            activity?.SetTag(VespaActivitySource.TagDocId, documentId);
        return activity;
    }

    private static string BuildUrl(string basePath, DocumentRequestOptions? requestOptions, params (string Key, string? Value)[] queryParams)
    {
        var sb = new System.Text.StringBuilder(basePath);
        var first = true;

        void Append((string Key, string? Value) p)
        {
            if (p.Value is null) return;
            sb.Append(first ? '?' : '&');
            first = false;
            sb.Append(p.Key).Append('=').Append(Uri.EscapeDataString(p.Value));
        }

        if (requestOptions is not null)
            foreach (var p in requestOptions.ToQueryParams())
                Append(p);

        foreach (var p in queryParams)
            Append(p);

        return sb.ToString();
    }

    private static async Task<VespaResponse> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
        {
            IReadOnlyList<string>? ignoredFields = null;
            if (response.Headers.TryGetValues("X-Vespa-Ignored-Fields", out var headerValues))
            {
                ignoredFields = headerValues
                    .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .ToList();
            }

            return new VespaResponse
            {
                IsSuccess = true,
                StatusCode = (int)response.StatusCode,
                Message = "Operation completed successfully",
                IgnoredFields = ignoredFields
            };
        }

        VespaError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<VespaError>(VespaJsonOptions.Default, cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            // fall through to raw content
        }

        var message = error?.Message ?? $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}";
        throw VespaException.FromStatusCode((int)response.StatusCode, message, error);
    }

    private async Task<VespaDocument<T>?> GetDocumentOrNullAsync<T>(
        string url,
        string? documentId,
        CancellationToken cancellationToken) where T : class
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (documentId is not null && logger != null)
                LogDocumentNotFound(logger, documentId);
            return null;
        }

        if (!response.IsSuccessStatusCode)
            throw BuildHttpError(response.StatusCode, "GET");

        return await VespaIdInjector.DeserializeAndInjectAsync<T>(response.Content, cancellationToken);
    }

    private static VespaException BuildHttpError(HttpStatusCode statusCode, string operation) =>
        VespaException.FromStatusCode((int)statusCode, $"{operation} failed with status {statusCode}");

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
