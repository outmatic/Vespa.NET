using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    /// <summary>
    /// Consecutive zero-progress selection chunks (same continuation token, zero
    /// documents) tolerated before the internal continuation loop gives up.
    /// </summary>
    private const int MaxStalledSelectionChunks = 64;
    private const string QueryParamCreate = "create";
    private const string QueryParamFieldSet = "fieldSet";
    private const string QueryParamSelection = "selection";
    private const string JsonlMediaType = "application/jsonl";
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

    public Task<VespaDocument<T>?> GetByGroupAsync<T>(
        string group,
        string localId,
        string documentType,
        string? @namespace = null,
        string? fieldSet = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
        => GetByAddressingAsync<T>("group", group, localId, documentType, @namespace, fieldSet, requestOptions, cancellationToken);

    public Task<VespaDocument<T>?> GetByNumberAsync<T>(
        long number,
        string localId,
        string documentType,
        string? @namespace = null,
        string? fieldSet = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
        => GetByAddressingAsync<T>("number", number.ToString(CultureInfo.InvariantCulture), localId, documentType, @namespace, fieldSet, requestOptions, cancellationToken);

    public Task<VespaResponse> PutByGroupAsync<T>(
        string group,
        string localId,
        T document,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        bool create = false,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
        => PutByAddressingAsync("group", group, localId, document, documentType, @namespace, condition, create, requestOptions, cancellationToken);

    public Task<VespaResponse> PutByNumberAsync<T>(
        long number,
        string localId,
        T document,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        bool create = false,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
        => PutByAddressingAsync("number", number.ToString(CultureInfo.InvariantCulture), localId, document, documentType, @namespace, condition, create, requestOptions, cancellationToken);

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
        => UpdateFieldsByAddressingAsync("group", group, localId, fieldOperations, documentType, @namespace, createIfMissing, condition, requestOptions, cancellationToken);

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
        => UpdateFieldsByAddressingAsync("number", number.ToString(CultureInfo.InvariantCulture), localId, fieldOperations, documentType, @namespace, createIfMissing, condition, requestOptions, cancellationToken);

    public Task<VespaResponse> DeleteByGroupAsync(
        string group,
        string localId,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => DeleteByAddressingAsync("group", group, localId, documentType, @namespace, condition, requestOptions, cancellationToken);

    public Task<VespaResponse> DeleteByNumberAsync(
        long number,
        string localId,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => DeleteByAddressingAsync("number", number.ToString(CultureInfo.InvariantCulture), localId, documentType, @namespace, condition, requestOptions, cancellationToken);

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
            (QueryParamFieldSet, fieldSet),
            (QueryParamCluster, requestOptions?.Cluster));

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
        bool create,
        DocumentRequestOptions? requestOptions,
        CancellationToken cancellationToken) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentNullException.ThrowIfNull(document);

        var (ns, url) = BuildAddressingUrl(scheme, bucket, localId, documentType, @namespace, requestOptions,
            (QueryParamCondition, condition),
            (QueryParamCreate, create ? "true" : null));

        using var activity = StartDocumentActivity(VespaActivitySource.DocumentPut, documentType, ns);

        return await SendPutAsync(url, document, cancellationToken);
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

        return await SendUpdateAsync(url, fieldOperations, cancellationToken);
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

        return await SendDeleteAsync(url, cancellationToken);
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
            $"{DocTypePrefix(documentType, ns)}/{scheme}/{Uri.EscapeDataString(bucket)}/{Uri.EscapeDataString(localId)}",
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

        return ExecuteSelectionOperationAsync(
            HttpMethod.Post, selection, documentType, @namespace, cluster,
            extraParams: [("destinationCluster", destinationCluster)],
            requestOptions: requestOptions,
            contentFactory: null,
            cancellationToken);
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
        var hadIgnoredFields = false;
        SelectionPageResult page;
        string? continuation = null;
        var stalledChunks = 0;

        do
        {
            // Cancellation propagates from the HTTP call/body read — exiting the loop
            // early would falsely report a partially-applied operation as successful.
            page = await ExecuteSelectionChunkAsync(
                method, selection, documentType, @namespace, cluster,
                extraParams, requestOptions, continuation, contentFactory, cancellationToken);
            totalDocumentCount += page.DocumentCount;
            hadIgnoredFields |= page.HadIgnoredFields;

            // Livelock guard: with a too-small timeChunk Vespa can return the exact
            // continuation token it was given, having processed zero documents — the
            // position never advances and this loop would hammer the server forever.
            if (page.Continuation is not null && page.Continuation == continuation && page.DocumentCount == 0)
            {
                if (++stalledChunks >= MaxStalledSelectionChunks)
                    throw new VespaException(
                        $"Selection operation made no progress over {MaxStalledSelectionChunks} consecutive chunks " +
                        "(Vespa returned the same continuation token with documentCount 0). " +
                        "Increase SelectionRequestOptions.TimeChunk so the server can complete at least one bucket per chunk.");
            }
            else
            {
                stalledChunks = 0;
            }

            continuation = page.Continuation;
        }
        while (continuation is not null);

        return new VespaResponse
        {
            IsSuccess = true,
            StatusCode = page.StatusCode,
            Message = "Selection operation completed successfully",
            DocumentCount = totalDocumentCount,
            HadIgnoredFields = hadIgnoredFields
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
        var optionParams = requestOptions?.ToQueryParams().ToArray() ?? [];
        var queryParams = new List<(string Key, string? Value)>(extraParams.Length + optionParams.Length + 3)
        {
            (QueryParamSelection, selection),
            (QueryParamCluster, cluster),
            (QueryParamContinuation, continuation)
        };
        queryParams.AddRange(extraParams);
        queryParams.AddRange(optionParams);

        var url = BuildUrl(DocBasePath(documentType, @namespace), null, [.. queryParams]);

        using var request = new HttpRequestMessage(method, url);
        if (contentFactory is not null)
            request.Content = contentFactory();

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw await VespaErrorResponse.CreateAsync(response, cancellationToken);

        var body = await TryReadSelectionBodyAsync(response, cancellationToken);
        return new SelectionPageResult
        {
            DocumentCount = body?.DocumentCount ?? 0,
            Continuation = body?.Continuation,
            StatusCode = (int)response.StatusCode,
            HadIgnoredFields = HasIgnoredFields(response)
        };
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
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed record SelectionResponseBody(
        [property: JsonPropertyName("documentCount")] long? DocumentCount,
        [property: JsonPropertyName("continuation")] string? Continuation,
        [property: JsonPropertyName("message")] string? Message);

    public IAsyncEnumerable<VespaDocument<T>> VisitJsonlAsync<T>(
        string documentType,
        string? selection = null,
        string? cluster = null,
        string? @namespace = null,
        VisitOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        return VisitJsonlCoreAsync<T>(VisitBasePath(documentType, @namespace, options), selection, cluster, options, cancellationToken);
    }

    public IAsyncEnumerable<VespaDocument<T>> VisitAsync<T>(
        string documentType,
        string? selection = null,
        string? cluster = null,
        string? @namespace = null,
        VisitOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        return VisitPagedCoreAsync<T>(VisitBasePath(documentType, @namespace, options), selection, cluster, options, cancellationToken);
    }

    public IAsyncEnumerable<VespaDocument<T>> VisitAllAsync<T>(
        string cluster,
        string? selection = null,
        VisitOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cluster);
        if (options is { Group: not null } or { Number: not null })
            throw new ArgumentException(
                "VisitOptions.Group/Number addressing is not supported on root-level visits.",
                nameof(options));

        return VisitPagedCoreAsync<T>($"{VespaPaths.DocV1}/", selection, cluster, options, cancellationToken);
    }

    private async IAsyncEnumerable<VespaDocument<T>> VisitPagedCoreAsync<T>(
        string basePath,
        string? selection,
        string? cluster,
        VisitOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class
    {
        string? continuation = null;
        do
        {
            var url = BuildVisitUrl(basePath, selection, cluster, options, continuation, stream: false);

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw await VespaErrorResponse.CreateAsync(response, cancellationToken);

            var page = await VespaIdInjector.DeserializeVisitAndInjectAsync<T>(response.Content, cancellationToken);
            if (page is null)
                yield break;

            foreach (var doc in page.Documents)
                yield return doc;

            continuation = page.Continuation;
        }
        while (continuation is not null);
    }

    /// <summary>
    /// Streams one JSONL visit chunk per HTTP request, auto-resuming on trailing
    /// continuation tokens (a Vespa-side visit timeout ends the stream with a
    /// <c>continuation</c> line that still carries a <c>token</c>). The visit is
    /// complete when the final continuation line has no token.
    /// </summary>
    private async IAsyncEnumerable<VespaDocument<T>> VisitJsonlCoreAsync<T>(
        string basePath,
        string? selection,
        string? cluster,
        VisitOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class
    {
        string? continuation = null;
        do
        {
            var url = BuildVisitUrl(basePath, selection, cluster, options, continuation, stream: true);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonlMediaType));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw await VespaErrorResponse.CreateAsync(response, cancellationToken);

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(mediaType, JsonlMediaType, StringComparison.OrdinalIgnoreCase))
                throw new VespaException(
                    $"JSONL visit expected an '{JsonlMediaType}' response but got '{mediaType ?? "(none)"}'. " +
                    "Streamed visits require Vespa 8.593 or newer — use VisitAsync against older versions.");

            continuation = null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonDocument json;
                try
                {
                    json = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    // Malformed/truncated line (e.g. connection drop mid-line) — skip.
                    continue;
                }

                using (json)
                {
                    var root = json.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        continue;

                    if (root.TryGetProperty("message", out var message))
                        throw new VespaException($"JSONL visit reported an error: {FormatVisitMessage(message)}");

                    if (root.TryGetProperty(QueryParamContinuation, out var marker))
                    {
                        // Continuation markers subsume earlier ones — remember only the
                        // last. It has no token (percentFinished == 100) iff complete.
                        continuation = marker.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String
                            ? token.GetString()
                            : null;
                        continue;
                    }

                    var doc = ParseJsonlDocument<T>(root);
                    if (doc is not null)
                        yield return doc;
                }
            }
        }
        while (continuation is not null);
    }

    private static string FormatVisitMessage(JsonElement message) => message.ValueKind switch
    {
        JsonValueKind.String => message.GetString() ?? string.Empty,
        JsonValueKind.Object when message.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String =>
            text.GetString() ?? message.GetRawText(),
        _ => message.GetRawText()
    };

    /// <summary>
    /// Parses one document/v1 JSONL document line. Document lines are
    /// <c>{"put":"id:...","fields":{...}}</c>, tombstones (only with
    /// <c>IncludeRemoves = true</c>) are <c>{"remove":"id:..."}</c> and yield a document
    /// with <see langword="null"/> <c>Fields</c>; other metadata returns <see langword="null"/>.
    /// </summary>
    private static VespaDocument<T>? ParseJsonlDocument<T>(JsonElement root) where T : class
    {
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

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string ResolveNamespace(string? @namespace) =>
        @namespace ?? _options.DefaultNamespace;

    /// <summary>
    /// Escaped <c>/document/v1/{namespace}/{documentType}</c> prefix. Both segments are
    /// escaped so user-supplied values cannot inject path segments (<c>../</c>, <c>/</c>).
    /// </summary>
    private static string DocTypePrefix(string documentType, string ns) =>
        $"{VespaPaths.DocV1}/{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(documentType)}";

    private string DocBasePath(string documentType, string? @namespace) =>
        $"{DocTypePrefix(documentType, ResolveNamespace(@namespace))}/docid/";

    /// <summary>
    /// Builds the URL for a by-document-ID operation. Full Vespa IDs
    /// (<c>id:ns:type:kv:user</c>) are parsed structurally: <c>g=</c>/<c>n=</c> location
    /// selectors route to the <c>group/…</c>/<c>number/…</c> path levels, and a namespace
    /// or document type differing from the operation's target throws
    /// <see cref="ArgumentException"/> instead of silently re-targeting another document.
    /// Bare IDs pass through unchanged.
    /// </summary>
    private (string Namespace, string Url) BuildDocIdUrl(
        string documentType,
        string documentId,
        string? @namespace,
        DocumentRequestOptions? requestOptions,
        params (string Key, string? Value)[] extraParams)
    {
        var ns = ResolveNamespace(@namespace);
        var prefix = DocTypePrefix(documentType, ns);
        string path;

        if (VespaDocumentId.TryParse(documentId, out var parts))
        {
            if (!string.Equals(parts.Namespace, ns, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Document ID '{documentId}' addresses namespace '{parts.Namespace}', but the operation targets namespace '{ns}'.",
                    nameof(documentId));
            if (!string.Equals(parts.DocumentType, documentType, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Document ID '{documentId}' addresses document type '{parts.DocumentType}', but the operation targets document type '{documentType}'.",
                    nameof(documentId));

            path = parts switch
            {
                { Group: not null } => $"{prefix}/group/{Uri.EscapeDataString(parts.Group)}/{Uri.EscapeDataString(parts.UserSpecified)}",
                { Number: not null } => $"{prefix}/number/{parts.Number}/{Uri.EscapeDataString(parts.UserSpecified)}",
                _ => $"{prefix}/docid/{Uri.EscapeDataString(parts.UserSpecified)}"
            };
        }
        else
            path = $"{prefix}/docid/{Uri.EscapeDataString(VespaDocumentId.GetUserSpecified(documentId))}";

        return (ns, BuildUrl(path, requestOptions, extraParams));
    }

    /// <summary>
    /// Resolves the visit base path: <c>docid/</c> by default, or the
    /// <c>group/{group}</c>/<c>number/{number}</c> level when set on
    /// <see cref="VisitOptions"/> (mutually exclusive).
    /// </summary>
    private string VisitBasePath(string documentType, string? @namespace, VisitOptions? options)
    {
        var prefix = DocTypePrefix(documentType, ResolveNamespace(@namespace));
        return options switch
        {
            { Group: not null, Number: not null } => throw new ArgumentException(
                "VisitOptions.Group and VisitOptions.Number are mutually exclusive.", nameof(options)),
            { Group: not null } => $"{prefix}/group/{Uri.EscapeDataString(options.Group)}",
            { Number: not null } => $"{prefix}/number/{options.Number}",
            _ => $"{prefix}/docid/"
        };
    }

    private static string BuildVisitUrl(
        string basePath,
        string? selection,
        string? cluster,
        VisitOptions? options,
        string? continuation,
        bool stream)
    {
        var queryParams = new List<(string Key, string? Value)>
        {
            (QueryParamCluster, cluster),
            (QueryParamSelection, selection)
        };
        if (options is not null)
            queryParams.AddRange(options.ToQueryParams(includeWantedDocumentCount: !stream));
        if (stream)
            queryParams.Add(("stream", "true"));
        queryParams.Add((QueryParamContinuation, continuation));

        return BuildUrl(basePath, null, [.. queryParams]);
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

    private static string BuildUrl(string basePath, DocumentRequestOptions? requestOptions, params (string Key, string? Value)[] queryParams)
    {
        var sb = new StringBuilder(basePath);
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
            {
                // Explicit method parameters win over request options — emitting the
                // same key twice (e.g. timeout) is undefined behaviour server-side.
                if (Array.Exists(queryParams, q => q.Value is not null && q.Key == p.Key))
                    continue;
                Append(p);
            }

        foreach (var p in queryParams)
            Append(p);

        return sb.ToString();
    }

    /// <summary>
    /// <c>X-Vespa-Ignored-Fields</c> is a boolean flag header — Vespa sets it to
    /// <c>true</c> when the operation contained ignored fields; it never lists them.
    /// </summary>
    private static bool HasIgnoredFields(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Vespa-Ignored-Fields", out var headerValues) &&
        headerValues.Any(v => string.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    private static async Task<VespaResponse> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        if (!response.IsSuccessStatusCode)
            throw await VespaErrorResponse.CreateAsync(response, cancellationToken);

        return new VespaResponse
        {
            IsSuccess = true,
            StatusCode = (int)response.StatusCode,
            Message = "Operation completed successfully",
            HadIgnoredFields = HasIgnoredFields(response)
        };
    }

    private async Task<VespaResponse> SendPutAsync<T>(string url, T document, CancellationToken cancellationToken) where T : class
    {
        var payload = new { fields = document };
        using var response = await _httpClient.PostAsJsonAsync(url, payload, VespaJsonOptions.Default, cancellationToken);
        return await ParseResponseAsync(response, cancellationToken);
    }

    private async Task<VespaResponse> SendUpdateAsync(string url, Dictionary<string, FieldOperation> fieldOperations, CancellationToken cancellationToken)
    {
        var payload = new { fields = fieldOperations };
        using var response = await _httpClient.PutAsJsonAsync(url, payload, VespaJsonOptions.Default, cancellationToken);
        return await ParseResponseAsync(response, cancellationToken);
    }

    private async Task<VespaResponse> SendDeleteAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.DeleteAsync(url, cancellationToken);
        return await ParseResponseAsync(response, cancellationToken);
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
            throw await VespaErrorResponse.CreateAsync(response, cancellationToken);

        return await VespaIdInjector.DeserializeAndInjectAsync<T>(response.Content, cancellationToken);
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
