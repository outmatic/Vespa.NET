using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vespa.Models;

namespace Vespa.Documents;

public sealed partial class DocumentOperations
{
    private const string JsonlMediaType = "application/jsonl";

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

    public Task<VespaVisitResponse<T>> VisitPageAsync<T>(
        string documentType,
        string? selection = null,
        string? cluster = null,
        string? @namespace = null,
        string? continuation = null,
        VisitOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        return VisitPageCoreAsync<T>(VisitBasePath(documentType, @namespace, options), selection, cluster, options, continuation, cancellationToken);
    }

    public Task<VespaVisitResponse<T>> VisitAllPageAsync<T>(
        string cluster,
        string? selection = null,
        string? continuation = null,
        VisitOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ValidateRootVisit(cluster, options);
        return VisitPageCoreAsync<T>($"{VespaPaths.DocV1}/", selection, cluster, options, continuation, cancellationToken);
    }

    public IAsyncEnumerable<VespaDocument<T>> VisitAllAsync<T>(
        string cluster,
        string? selection = null,
        VisitOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ValidateRootVisit(cluster, options);

        return VisitPagedCoreAsync<T>($"{VespaPaths.DocV1}/", selection, cluster, options, cancellationToken);
    }

    private static void ValidateRootVisit(string cluster, VisitOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cluster);
        if (options is { Group: not null } or { Number: not null })
            throw new ArgumentException(
                "VisitOptions.Group/Number addressing is not supported on root-level visits.",
                nameof(options));
    }

    private async Task<VespaVisitResponse<T>> VisitPageCoreAsync<T>(
        string basePath,
        string? selection,
        string? cluster,
        VisitOptions? options,
        string? continuation,
        CancellationToken cancellationToken) where T : class
    {
        var url = BuildVisitUrl(basePath, selection, cluster, options, continuation, stream: false);
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw await VespaErrorResponse.CreateAsync(response, cancellationToken);

        return await VespaIdInjector.DeserializeVisitAndInjectAsync<T>(response.Content, cancellationToken)
            ?? throw new VespaException("The visit response did not contain a page.");
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
            var page = await VisitPageCoreAsync<T>(basePath, selection, cluster, options, continuation, cancellationToken);

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
            var endedWithContinuation = false;

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
                catch (JsonException ex)
                {
                    throw new VespaException("Invalid JSONL visit response; the stream may be incomplete.", ex);
                }

                using (json)
                {
                    var root = json.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                        throw new VespaException("Invalid JSONL visit response: each line must be a JSON object.");

                    endedWithContinuation = false;

                    if (root.TryGetProperty("message", out var message))
                    {
                        HandleVisitMessage(message);
                        continue;
                    }

                    if (root.TryGetProperty(QueryParamContinuation, out var marker))
                    {
                        continuation = ReadVisitContinuation(marker);
                        endedWithContinuation = true;
                        continue;
                    }

                    var doc = ParseJsonlDocument<T>(root);
                    if (doc is not null)
                        yield return doc;
                }
            }

            if (!endedWithContinuation)
                throw new VespaException("JSONL visit ended without a final continuation marker; the stream may be incomplete.");
        }
        while (continuation is not null);
    }

    private static string? ReadVisitContinuation(JsonElement marker)
    {
        if (marker.ValueKind != JsonValueKind.Object)
            throw new VespaException("Invalid JSONL visit continuation: expected an object.");

        if (marker.TryGetProperty("token", out var token) && token.ValueKind != JsonValueKind.Null)
        {
            if (token.ValueKind == JsonValueKind.String && token.GetString() is { Length: > 0 } value)
                return value;
            throw new VespaException("Invalid JSONL visit continuation token.");
        }

        if (marker.TryGetProperty("percentFinished", out var percent) &&
            percent.ValueKind == JsonValueKind.Number && percent.TryGetDouble(out var valueFinished) && valueFinished == 100)
            return null;

        throw new VespaException("JSONL visit continuation has no resume token and does not report 100 percent completion.");
    }

    private void HandleVisitMessage(JsonElement message)
    {
        var text = FormatVisitMessage(message);
        var severity = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("severity", out var level) &&
            level.ValueKind == JsonValueKind.String ? level.GetString() : null;

        if (severity is not ("info" or "warning"))
            throw new VespaException($"JSONL visit reported an error: {text}");

        if (logger is null)
            return;
        if (severity == "warning")
            LogVisitWarning(logger, text);
        else
            LogVisitInformation(logger, text);
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
                FullId = removeElem.GetString() ?? string.Empty
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
            FullId = id,
            Fields = root.TryGetProperty("fields", out var fieldsElem)
                ? fieldsElem.Deserialize<T>(VespaJsonOptions.Default)
                : null
        };
        VespaIdInjector.Inject(doc);
        return doc;
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

    [LoggerMessage(EventId = 106, Level = LogLevel.Warning, Message = "JSONL visit: {Message}")]
    static partial void LogVisitWarning(ILogger logger, string message);

    [LoggerMessage(EventId = 107, Level = LogLevel.Information, Message = "JSONL visit: {Message}")]
    static partial void LogVisitInformation(ILogger logger, string message);
}
