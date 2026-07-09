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
    /// Execute a nearest neighbor search using embeddings.
    /// The generated YQL uses the <c>totalTargetHits</c> annotation with
    /// <paramref name="topK"/> as its value.
    /// </summary>
    /// <param name="queryEmbedding">Dense query vector.</param>
    /// <param name="embeddingField">Tensor field to search.</param>
    /// <param name="documentType">Document type to search.</param>
    /// <param name="topK">Number of neighbors to retrieve and return.</param>
    /// <param name="filter">
    /// Optional raw YQL predicate AND'ed with the nearest-neighbor operator.
    /// <b>Warning:</b> embedded verbatim without validation or escaping — do not pass
    /// unvalidated end-user input; build it with <c>YqlBuilder</c> where possible.
    /// </param>
    /// <param name="rankProfile">Optional rank profile name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<VespaSearchResponse<T>> NearestNeighborSearchAsync<T>(
        VespaTensor queryEmbedding,
        string embeddingField,
        string documentType,
        int topK = 10,
        string? filter = null,
        string? rankProfile = null,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Execute a simple YQL query.
    /// <paramref name="parameters"/> are sent as top-level request properties,
    /// so they resolve <c>userInput(@param)</c> references in the YQL.
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
    /// <para>
    /// <b>Server limits:</b> Vespa caps <c>hits</c> at <c>maxHits</c> (default 400 —
    /// a larger <paramref name="pageSize"/> is a 400 error) and <c>offset</c> at
    /// <c>maxOffset</c> (default 1000), so deep pagination fails unless those query-profile
    /// limits are raised. To export a full corpus, use document visiting
    /// (<c>Documents.VisitAsync</c>/<c>VisitJsonlAsync</c>) instead of search pagination.
    /// </para>
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
