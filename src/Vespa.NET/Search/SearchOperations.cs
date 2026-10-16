using System.Diagnostics;
using System.Globalization;
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
                var result = await VespaIdInjector.DeserializeSearchAndInjectAsync<T>(response.Content, cancellationToken)
                    ?? throw new VespaException("Failed to deserialize search response");

                var totalCount = result.Root.Fields?.TotalCount ?? 0;
                activity?.SetTag(VespaActivitySource.TagTotalCount, totalCount);

                if (logger != null)
                    LogSearchCompleted(logger, totalCount, result.Root.Children.Count);

                return result;
            },
            cancellationToken);
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

        var values = ExtractDenseValues(queryEmbedding);

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
            CustomParameters = parameters
        };

        return await SearchAsync<T>(request, cancellationToken);
    }

    public async Task<GroupingSearchResponse<T>> GroupByAsync<T>(
        VespaSearchRequest request,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Yql, nameof(request));

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
                return GroupingResponseParser.Parse<T>(doc.RootElement);
            },
            cancellationToken);
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
        CancellationToken cancellationToken)
    {
        try
        {
            // ResponseHeadersRead lets large result sets stream into the JSON parser
            // instead of buffering the whole body first. The error path is safe: it
            // reads the (unbuffered) body to a string exactly once.
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, VespaPaths.Search)
            {
                Content = JsonContent.Create(request, options: VespaJsonOptions.Default)
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

    private static double[] ExtractDenseValues(VespaTensor queryEmbedding)
    {
        if (queryEmbedding.GetDenseValues<double>() is { } doubleValues)
            return doubleValues;
        if (queryEmbedding.GetDenseValues<float>() is { } floatValues)
            return Array.ConvertAll(floatValues, x => (double)x);
        if (queryEmbedding.GetDenseValues<sbyte>() is { } sbyteValues)
            return Array.ConvertAll(sbyteValues, x => (double)x);
        if (queryEmbedding.GetDenseValues<Half>() is { } halfValues)
            return Array.ConvertAll(halfValues, x => (double)x);

        throw new ArgumentException("Unable to extract dense values from tensor", nameof(queryEmbedding));
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
            Input = new Dictionary<string, object>
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
