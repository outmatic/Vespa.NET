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
                IReadOnlyList<string>? continuationTokens = null;

                foreach (var child in children.EnumerateArray())
                {
                    if (!child.TryGetProperty("id", out var idEl)) continue;
                    var id = idEl.GetString() ?? "";

                    if (id.StartsWith("group:root:", StringComparison.Ordinal))
                    {
                        groupingResults.AddRange(ParseGroupLists<T>(child));
                        continuationTokens ??= ExtractContinuationTokens(child);
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

                return new GroupingSearchResponse<T>(hits, groupingResults, totalCount, timing, continuationTokens);
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

    /// <summary>
    /// Collects grouping continuation tokens from a <c>group:root</c> node:
    /// the root's <c>continuation.this</c> first, then every group list's
    /// <c>continuation.next</c> (recursively). Returns <see langword="null"/> when
    /// no list has a next page.
    /// </summary>
    private static List<string>? ExtractContinuationTokens(JsonElement groupRootNode)
    {
        var nextTokens = new List<string>();
        CollectNextTokens(groupRootNode, nextTokens);
        if (nextTokens.Count == 0)
            return null;

        var tokens = new List<string>();
        // Vespa renders "this" as an empty string on the first page — skip it
        if (groupRootNode.TryGetProperty("continuation", out var contEl) &&
            contEl.TryGetProperty("this", out var thisEl) &&
            thisEl.GetString() is { Length: > 0 } thisToken)
            tokens.Add(thisToken);
        tokens.AddRange(nextTokens);
        return tokens;
    }

    private static void CollectNextTokens(JsonElement node, List<string> output)
    {
        if (node.TryGetProperty("id", out var idEl) &&
            (idEl.GetString() ?? "").StartsWith("grouplist:", StringComparison.Ordinal) &&
            node.TryGetProperty("continuation", out var contEl) &&
            contEl.TryGetProperty("next", out var nextEl) &&
            nextEl.GetString() is { Length: > 0 } token)
            output.Add(token);

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                CollectNextTokens(child, output);
    }

    private static List<VespaGroupList> ParseGroupLists<T>(JsonElement groupRootNode) where T : class
    {
        var result = new List<VespaGroupList>();
        if (!groupRootNode.TryGetProperty("children", out var children)) return result;

        foreach (var child in children.EnumerateArray())
        {
            if (!child.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.GetString() ?? "";
            if (id.StartsWith("grouplist:", StringComparison.Ordinal))
                result.Add(ParseSingleGroupList<T>(child, id["grouplist:".Length..]));
        }
        return result;
    }

    private static VespaGroupList ParseSingleGroupList<T>(JsonElement groupListNode, string label) where T : class
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
                var rawAggregations = new Dictionary<string, JsonElement>();

                if (groupNode.TryGetProperty("fields", out var aggFields))
                    foreach (var field in aggFields.EnumerateObject())
                    {
                        // Clone detaches the element from the JsonDocument disposed after parsing
                        rawAggregations[field.Name] = field.Value.Clone();
                        if (field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetDouble(out var d))
                            aggregations[field.Name] = d;
                    }

                var subGroups = (IReadOnlyList<VespaGroupList>)[];
                var hits = (IReadOnlyList<object>)[];
                if (groupNode.TryGetProperty("children", out var subChildren))
                    (subGroups, hits) = ParseGroupChildren<T>(subChildren);

                groups.Add(new VespaGroup(value, aggregations, subGroups)
                {
                    Hits = hits,
                    RawAggregations = rawAggregations
                });
            }
        }

        return new VespaGroupList(label, groups);
    }

    /// <summary>
    /// A group node's children mix nested groupings (<c>grouplist:*</c>) with
    /// per-group document summaries (<c>hitlist:*</c>, from <c>each(output(summary()))</c>).
    /// </summary>
    private static (IReadOnlyList<VespaGroupList> SubGroups, IReadOnlyList<object> Hits) ParseGroupChildren<T>(
        JsonElement childrenEl) where T : class
    {
        var subGroups = new List<VespaGroupList>();
        var hits = new List<object>();

        foreach (var child in childrenEl.EnumerateArray())
        {
            if (!child.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.GetString() ?? "";

            if (id.StartsWith("grouplist:", StringComparison.Ordinal))
            {
                subGroups.Add(ParseSingleGroupList<T>(child, id["grouplist:".Length..]));
            }
            else if (id.StartsWith("hitlist:", StringComparison.Ordinal) && child.TryGetProperty("children", out var hitChildren))
            {
                foreach (var hitNode in hitChildren.EnumerateArray())
                {
                    var hit = hitNode.Deserialize<SearchHit<T>>(VespaJsonOptions.Default);
                    if (hit is null) continue;
                    VespaIdInjector.Inject(hit);
                    hits.Add(hit);
                }
            }
        }

        return (subGroups, hits);
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
