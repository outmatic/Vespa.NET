using System.Diagnostics;
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
        activity?.SetTag(VespaActivitySource.TagYql, request.Yql);
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
        string? @namespace = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(queryEmbedding);

        if (queryEmbedding.Format != TensorFormat.IndexedDense)
            throw new ArgumentException($"Unsupported tensor format for nearest neighbor search: {queryEmbedding.Format}. Use IndexedDense format.", nameof(queryEmbedding));

        var values = ExtractDenseValues(queryEmbedding);

        if (values.Length == 0)
            throw new ArgumentException("Query embedding values cannot be empty", nameof(queryEmbedding));

        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingField);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        if (logger != null)
            LogExecutingNearestNeighborSearch(logger, documentType, values.Length, topK);

        using var activity = VespaActivitySource.Instance.StartActivity(VespaActivitySource.NearestNeighbor);
        activity?.SetTag(VespaActivitySource.TagDocType, documentType);
        activity?.SetTag(VespaActivitySource.TagNamespace, @namespace ?? _options.DefaultNamespace);
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

        var request = new VespaSearchRequest
        {
            Yql = yql,
            Hits = hits,
            Offset = offset,
            Input = parameters
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
        activity?.SetTag(VespaActivitySource.TagYql, request.Yql);

        return await ExecuteSearchRequestAsync(
            request,
            activity,
            async response =>
            {
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var rootEl = doc.RootElement;

                TimingInfo? timing = rootEl.TryGetProperty("timing", out var timingEl)
                    ? timingEl.Deserialize<TimingInfo>(VespaJsonOptions.Default)
                    : null;

                if (!rootEl.TryGetProperty("root", out var searchRoot))
                    return new GroupingSearchResponse<T>([], [], 0, timing, null);

                long totalCount = searchRoot.TryGetProperty("fields", out var fields) &&
                    fields.TryGetProperty("totalCount", out var tc)
                        ? tc.GetInt64()
                        : 0;

                if (!searchRoot.TryGetProperty("children", out var children))
                    return new GroupingSearchResponse<T>([], [], totalCount, timing, null);

                var hits = new List<SearchHit<T>>();
                var groupingResults = new List<VespaGroupList>();
                string? continuation = null;

                foreach (var child in children.EnumerateArray())
                {
                    if (!child.TryGetProperty("id", out var idEl)) continue;
                    var id = idEl.GetString() ?? "";

                    if (id.StartsWith("group:root:"))
                    {
                        groupingResults.AddRange(ParseGroupLists(child));
                        if (continuation is null &&
                            child.TryGetProperty("continuation", out var contEl) &&
                            contEl.TryGetProperty("next", out var nextEl))
                            continuation = nextEl.GetString();
                    }
                    else
                    {
                        var hit = child.Deserialize<SearchHit<T>>(VespaJsonOptions.Default);
                        if (hit is not null)
                        {
                            VespaIdInjector.Inject(hit);
                            hits.Add(hit);
                        }
                    }
                }

                return new GroupingSearchResponse<T>(hits, groupingResults, totalCount, timing, continuation);
            },
            cancellationToken);
    }

    private async Task<TResult> ExecuteSearchRequestAsync<TResult>(
        VespaSearchRequest request,
        Activity? activity,
        Func<HttpResponseMessage, Task<TResult>> parseResponse,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(VespaPaths.Search, request, VespaJsonOptions.Default, cancellationToken);
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

        var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
        activity?.SetStatus(ActivityStatusCode.Error, errorContent);
        throw VespaException.FromStatusCode(
            (int)response.StatusCode,
            $"Search failed with status {response.StatusCode}: {errorContent}");
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
            yqlBuilder.Append($"({filter}) and ");

        var queryTensorName = $"query_{embeddingField}";
        yqlBuilder.Append($"({{targetHits: {topK}}}nearestNeighbor({embeddingField}, {queryTensorName}))");

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
        activity?.SetTag(VespaActivitySource.TagYql, request.Yql);

        var offset = request.Offset;

        while (true)
        {
            var page = request.ShallowClone();
            page.Hits = pageSize;
            page.Offset = offset;

            var response = await SearchAsync<T>(page, cancellationToken);
            var hits = response.Root.Children;

            foreach (var hit in hits)
                yield return hit;

            if (hits.Count < pageSize)
                yield break;

            offset += hits.Count;
        }
    }

    private static IReadOnlyList<VespaGroupList> ParseGroupLists(JsonElement groupRootNode)
    {
        var result = new List<VespaGroupList>();
        if (!groupRootNode.TryGetProperty("children", out var children)) return result;

        foreach (var child in children.EnumerateArray())
        {
            if (!child.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.GetString() ?? "";
            if (id.StartsWith("grouplist:"))
                result.Add(ParseSingleGroupList(child, id["grouplist:".Length..]));
        }
        return result;
    }

    private static VespaGroupList ParseSingleGroupList(JsonElement groupListNode, string label)
    {
        var groups = new List<VespaGroup>();

        if (groupListNode.TryGetProperty("children", out var groupChildren))
        {
            foreach (var groupNode in groupChildren.EnumerateArray())
            {
                var groupId = groupNode.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";

                // Skip rest buckets (group:null)
                if (groupId is "group:null")
                    continue;

                var value = groupNode.TryGetProperty("value", out var v)
                    ? v.ToString()
                    : ParseBucketId(groupId);

                var aggregations = new Dictionary<string, double>();

                if (groupNode.TryGetProperty("fields", out var aggFields))
                    foreach (var field in aggFields.EnumerateObject())
                        if (field.Value.TryGetDouble(out var d))
                            aggregations[field.Name] = d;

                var subGroups = groupNode.TryGetProperty("children", out var subChildren)
                    ? ParseSubGroupLists(subChildren)
                    : (IReadOnlyList<VespaGroupList>)[];

                groups.Add(new VespaGroup(value, aggregations, subGroups));
            }
        }

        return new VespaGroupList(label, groups);
    }

    private static IReadOnlyList<VespaGroupList> ParseSubGroupLists(JsonElement childrenEl)
    {
        var result = new List<VespaGroupList>();
        foreach (var child in childrenEl.EnumerateArray())
        {
            if (!child.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.GetString() ?? "";
            if (id.StartsWith("grouplist:"))
                result.Add(ParseSingleGroupList(child, id["grouplist:".Length..]));
        }
        return result;
    }

    /// <summary>
    /// Extracts "from:to" from bucket group IDs like "group:long_bucket:0:100"
    /// or "group:double_bucket:0.0:99.9" or "group:string_bucket:a:m".
    /// </summary>
    private static string ParseBucketId(string groupId)
    {
        // Format: "group:{type}_bucket:{from}:{to}"
        const string bucketMarker = "_bucket:";
        var bucketIdx = groupId.IndexOf(bucketMarker, StringComparison.Ordinal);
        if (bucketIdx < 0)
            return groupId;

        var rangeStart = bucketIdx + bucketMarker.Length;
        if (rangeStart >= groupId.Length)
            return groupId;

        // The range portion is "from:to" — return as-is
        return groupId[rangeStart..];
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Executing search query: {Yql}")]
    static partial void LogExecutingSearch(ILogger logger, string yql);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Search completed. Total hits: {TotalCount}, Returned: {ReturnedCount}")]
    static partial void LogSearchCompleted(ILogger logger, long totalCount, int returnedCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "Executing nearest neighbor search for {DocumentType} with embedding dimension {Dimension}, topK={TopK}")]
    static partial void LogExecutingNearestNeighborSearch(ILogger logger, string documentType, int dimension, int topK);
}
