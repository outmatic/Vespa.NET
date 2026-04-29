using Vespa.Models;
using Vespa.Models.Tensors;

namespace Vespa.Search;

/// <summary>
/// Interface for search operations
/// </summary>
public interface ISearchOperations
{
    /// <summary>
    /// Execute a search query
    /// </summary>
    Task<VespaSearchResponse<T>> SearchAsync<T>(
        VespaSearchRequest request,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Execute a nearest neighbor search using embeddings
    /// </summary>
    Task<VespaSearchResponse<T>> NearestNeighborSearchAsync<T>(
        VespaTensor queryEmbedding,
        string embeddingField,
        string documentType,
        int topK = 10,
        string? filter = null,
        string? rankProfile = null,
        string? @namespace = null,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Execute a simple YQL query
    /// </summary>
    Task<VespaSearchResponse<T>> QueryAsync<T>(
        string yql,
        int hits = 10,
        int offset = 0,
        Dictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Execute a search with grouping/aggregation and return both hits and grouped results.
    /// The <paramref name="request"/> YQL must include a pipe grouping expression
    /// (e.g., built with <c>YqlBuilder.GroupBy(GroupingBuilder.All()...)</c>).
    /// </summary>
    Task<GroupingSearchResponse<T>> GroupByAsync<T>(
        VespaSearchRequest request,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Stream all matching hits by transparently paginating through the result set.
    /// Each page is fetched lazily as the caller iterates.
    /// </summary>
    /// <param name="request">
    /// Template request. <c>Hits</c> is overridden by <paramref name="pageSize"/>;
    /// <c>Offset</c> is used as the starting position.
    /// </param>
    /// <param name="pageSize">Number of hits to fetch per HTTP request (default 100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    IAsyncEnumerable<SearchHit<T>> SearchStreamAsync<T>(
        VespaSearchRequest request,
        int pageSize = 100,
        CancellationToken cancellationToken = default
    ) where T : class;
}
