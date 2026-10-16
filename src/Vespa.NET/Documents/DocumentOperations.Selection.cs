using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vespa.Models;

namespace Vespa.Documents;

public sealed partial class DocumentOperations
{
    /// <summary>
    /// Consecutive zero-progress selection chunks (same continuation token, zero
    /// documents) tolerated before the internal continuation loop gives up.
    /// </summary>
    private const int MaxStalledSelectionChunks = 64;
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

        var body = await ReadSelectionBodyAsync(response, cancellationToken);
        return new SelectionPageResult
        {
            DocumentCount = body.DocumentCount!.Value,
            Continuation = body.Continuation,
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

    private static async Task<SelectionResponseBody> ReadSelectionBodyAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        SelectionResponseBody? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<SelectionResponseBody>(VespaJsonOptions.Default, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException)
        {
            throw new VespaException("Could not read the selection response; the operation may have been partially applied.", ex);
        }

        if (!string.IsNullOrWhiteSpace(body?.Message))
            throw new VespaException($"Selection operation failed: {body.Message}");
        if (body?.DocumentCount is not >= 0 || body.Continuation is { Length: 0 })
            throw new VespaException("Invalid selection response; a documentCount and a valid continuation are required to determine progress.");

        return body;
    }

    private sealed record SelectionResponseBody(
        [property: JsonPropertyName("documentCount")] long? DocumentCount,
        [property: JsonPropertyName("continuation")] string? Continuation,
        [property: JsonPropertyName("message")] string? Message);
}
