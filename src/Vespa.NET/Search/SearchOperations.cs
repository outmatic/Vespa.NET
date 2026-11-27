using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Vespa.Models;
using Vespa.Models.Tensors;

namespace Vespa.Search;

/// <summary>
/// Implementation of search operations
/// </summary>
public sealed partial class SearchOperations(
    HttpClient httpClient,
    VespaClientOptions options,
    ILogger? logger = null) : ISearchOperations
{
    private readonly HttpClient _httpClient =
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly VespaClientOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async Task<VespaSearchResponse<T>> SearchAsync<T>(
        VespaSearchRequest request,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Yql, nameof(request));
        var model = VespaJsonModels.Resolve<T>(_options.JsonModels, "search");

        using var activity = VespaActivitySource.Instance.StartActivity(VespaActivitySource.Search);
        RecordYql(activity, request);
        activity?.SetTag(VespaActivitySource.TagHits, request.Hits);

        if (logger != null)
            LogExecutingSearch(logger, request.Yql);

        return await ExecuteSearchRequestAsync(
            request,
            activity,
            async response =>
            {
                var result = VespaJsonReflection.IsEnabled && model is null
                    ? await VespaIdInjector.DeserializeSearchAndInjectAsync<T>(response.Content, cancellationToken)
                    : (await response.Content.ReadFromJsonAsync(
                        VespaProtocolJsonContext.Default.SearchResponseWire, cancellationToken))?.ToResponse(model!);
                if (result is null)
                    throw new VespaException("Failed to deserialize search response");

                var totalCount = result.Root.Fields?.TotalCount ?? 0;
                activity?.SetTag(VespaActivitySource.TagTotalCount, totalCount);

                if (logger != null)
                    LogSearchCompleted(logger, totalCount, result.Root.Children.Count);

                return result;
            },
            cancellationToken,
            useGeneratedMetadata: model is not null);
    }

    public async Task<VespaSearchResponse<T>> NearestNeighborSearchAsync<T>(
        VespaTensor queryEmbedding,
        string embeddingField,
        string documentType,
        int topK = 10,
        string? filter = null,
        string? rankProfile = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(queryEmbedding);

        if (queryEmbedding.Format != TensorFormat.IndexedDense)
            throw new ArgumentException($"Unsupported tensor format for nearest neighbor search: {queryEmbedding.Format}. Use IndexedDense format.", nameof(queryEmbedding));

        var values = queryEmbedding.GetDenseValues<double>()
            ?? throw new ArgumentException("Unable to extract dense values from tensor", nameof(queryEmbedding));

        if (values.Length == 0)
            throw new ArgumentException("Query embedding values cannot be empty", nameof(queryEmbedding));

        Query.YqlIdentifier.Validate(embeddingField, nameof(embeddingField));
        Query.YqlIdentifier.Validate(documentType, nameof(documentType));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        if (logger != null)
            LogExecutingNearestNeighborSearch(logger, documentType, values.Length, topK);

        using var activity = VespaActivitySource.Instance.StartActivity(VespaActivitySource.NearestNeighbor);
        activity?.SetTag(VespaActivitySource.TagDocType, documentType);
        activity?.SetTag(VespaActivitySource.TagEmbeddingField, embeddingField);
        activity?.SetTag(VespaActivitySource.TagTopK, topK);

        var request = BuildNearestNeighborRequest(values, embeddingField, documentType, topK, filter, rankProfile);
        return await SearchAsync<T>(request, cancellationToken);
    }

    public async Task<VespaSearchResponse<T>> QueryAsync<T>(
        string yql,
        int hits = 10,
        int offset = 0,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yql);

        // Parameters must be top-level request properties: userInput(@param) and
        // userQuery() resolve "param", not "input.param". Tensor inputs go through
        // VespaSearchRequest.Input / WithQueryTensor instead.
        var request = new VespaSearchRequest
        {
            Yql = yql,
            Hits = hits,
            Offset = offset,
            CustomParameters = parameters?.ToImmutableDictionary(
                static pair => pair.Key, static pair => (object?)pair.Value, parameters.Comparer)
        };

        return await SearchAsync<T>(request, cancellationToken);
    }

    public async Task<GroupingSearchResponse<T>> GroupByAsync<T>(
        VespaSearchRequest request,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Yql, nameof(request));
        var model = VespaJsonModels.Resolve<T>(_options.JsonModels, "grouping");

        if (logger != null)
            LogExecutingSearch(logger, request.Yql);

        using var activity = VespaActivitySource.Instance.StartActivity(VespaActivitySource.SearchGroup);
        RecordYql(activity, request);

        return await ExecuteSearchRequestAsync(
            request,
            activity,
            async response =>
            {
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                return GroupingResponseParser.Parse(doc.RootElement, model);
            },
            cancellationToken,
            useGeneratedMetadata: model is not null);
    }

    /// <summary>
    /// YQL frequently embeds user-supplied search terms — only export it to tracing
    /// backends when explicitly opted in (<see cref="VespaClientOptions.RecordQueryTextInTraces"/>).
    /// </summary>
    private void RecordYql(Activity? activity, VespaSearchRequest request)
    {
        if (_options.RecordQueryTextInTraces)
            activity?.SetTag(VespaActivitySource.TagYql, request.Yql);
    }

    private async Task<TResult> ExecuteSearchRequestAsync<TResult>(
        VespaSearchRequest request,
        Activity? activity,
        Func<HttpResponseMessage, Task<TResult>> parseResponse,
        CancellationToken cancellationToken,
        bool useGeneratedMetadata = false)
    {
        try
        {
            // ResponseHeadersRead lets large result sets stream into the JSON parser
            // instead of buffering the whole body first. The error path is safe: it
            // reads the (unbuffered) body to a string exactly once.
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, VespaPaths.Search)
            {
                Version = _httpClient.DefaultRequestVersion,
                VersionPolicy = _httpClient.DefaultVersionPolicy,
                Content = CreateSearchContent(request, useGeneratedMetadata)
            };
            using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureSearchSuccessAsync(response, activity, cancellationToken);
            return await parseResponse(response);
        }
        catch (Exception ex) when (ex is not VespaException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The reflection-disabled path creates search content with the generated protocol context.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The reflection-disabled path serializes the request using generated JsonTypeInfo.")]
    private static HttpContent CreateSearchContent(VespaSearchRequest request, bool useGeneratedMetadata)
    {
        if (VespaJsonReflection.IsEnabled && !useGeneratedMetadata)
            return JsonContent.Create(request, options: VespaJsonOptions.Default);

        // Resolve any runtime parameter types before the transport starts the request.
        var json = JsonSerializer.SerializeToUtf8Bytes(request, VespaProtocolJsonContext.Default.VespaSearchRequest);
        return new ByteArrayContent(json)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json", "utf-8") }
        };
    }

    private static async Task EnsureSearchSuccessAsync(
        HttpResponseMessage response,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var exception = await VespaErrorResponse.CreateAsync(response, cancellationToken, context: "Search failed");
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        throw exception;
    }

    private static VespaSearchRequest BuildNearestNeighborRequest(
        double[] values,
        string embeddingField,
        string documentType,
        int topK,
        string? filter,
        string? rankProfile)
    {
        var yqlBuilder = new StringBuilder($"select * from {documentType} where ");

        if (!string.IsNullOrWhiteSpace(filter))
            yqlBuilder.Append(CultureInfo.InvariantCulture, $"({filter}) and ");

        var queryTensorName = $"query_{embeddingField}";
        // totalTargetHits (hits over all content nodes) is the documented, preferred annotation
        yqlBuilder.Append(CultureInfo.InvariantCulture, $"({{totalTargetHits: {topK}}}nearestNeighbor({embeddingField}, {queryTensorName}))");

        return new VespaSearchRequest
        {
            Yql = yqlBuilder.ToString(),
            Hits = topK,
            Ranking = !string.IsNullOrWhiteSpace(rankProfile)
                ? new RankingConfig { Profile = rankProfile }
                : null,
            Input = new Dictionary<string, object?>
            {
                [$"query({queryTensorName})"] = new Dictionary<string, object>
                {
                    ["values"] = values
                }
            }
        };
    }

    public async IAsyncEnumerable<SearchHit<T>> SearchStreamAsync<T>(
        VespaSearchRequest request,
        int pageSize = 100,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        using var activity = VespaActivitySource.Instance.StartActivity(VespaActivitySource.SearchStream);
        RecordYql(activity, request);

        await foreach (var page in this.SearchPagedAsync<T>(request, pageSize, cancellationToken))
            foreach (var hit in page.Root.Children)
                yield return hit;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Executing search query: {Yql}")]
    static partial void LogExecutingSearch(ILogger logger, string yql);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Search completed. Total hits: {TotalCount}, Returned: {ReturnedCount}")]
    static partial void LogSearchCompleted(ILogger logger, long totalCount, int returnedCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Executing nearest neighbor search for {DocumentType} with embedding dimension {Dimension}, topK={TopK}")]
    static partial void LogExecutingNearestNeighborSearch(ILogger logger, string documentType, int dimension, int topK);
}
