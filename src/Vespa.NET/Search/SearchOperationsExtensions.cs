using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Models.Tensors;

namespace Vespa.Search;

/// <summary>
/// Extension methods on <see cref="ISearchOperations"/> that infer
/// <c>documentType</c> and <c>@namespace</c> from <c>[VespaDocument]</c> on the model type.
/// </summary>
public static class SearchOperationsExtensions
{
    /// <summary>
    /// Streams all pages of a grouping search, automatically passing
    /// <see cref="GroupingSearchResponse{T}.ContinuationTokens"/> back through the YQL
    /// <c>continuations</c> annotation until no more pages remain.
    /// Each yielded value is one page of grouped results.
    /// </summary>
    /// <example>
    /// <code>
    /// await foreach (var page in client.Search.GroupByStreamAsync&lt;Music&gt;(request))
    ///     foreach (var list in page.GroupingResults)
    ///         Console.WriteLine(list.Label);
    /// </code>
    /// </example>
    public static async IAsyncEnumerable<GroupingSearchResponse<T>> GroupByStreamAsync<T>(
        this ISearchOperations ops,
        VespaSearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class
    {
        IReadOnlyList<string>? tokens = null;
        while (true)
        {
            // Clone per page — mutating the caller's request (or its YQL) would leave
            // a stale continuation annotation behind after enumeration.
            var pageRequest = request.ShallowClone();
            if (tokens is not null)
                pageRequest.Yql = GroupingContinuations.Apply(request.Yql!, tokens);

            var page = await ops.GroupByAsync<T>(pageRequest, cancellationToken);
            yield return page;

            var next = page.ContinuationTokens;
            // Identical tokens would re-fetch the same page forever — treat as done.
            if (next is null || (tokens is not null && next.SequenceEqual(tokens)))
                yield break;
            tokens = next;
        }
    }

    /// <summary>
    /// Iterates search results page by page, yielding each full <see cref="VespaSearchResponse{T}"/>.
    /// Stops automatically when a page returns fewer hits than <paramref name="pageSize"/>.
    /// </summary>
    public static async IAsyncEnumerable<VespaSearchResponse<T>> SearchPagedAsync<T>(
        this ISearchOperations ops,
        VespaSearchRequest request,
        int pageSize = 100,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        var offset = request.Offset;
        int returned;
        do
        {
            // Clone per page — mutating the caller's request would make a second
            // enumeration silently start past the last page.
            var pageRequest = request.ShallowClone();
            pageRequest.Hits = pageSize;
            pageRequest.Offset = offset;
            var page = await ops.SearchAsync<T>(pageRequest, cancellationToken);
            yield return page;
            returned = page.Root.Children.Count;
            offset += returned;
        }
        while (returned == pageSize);
    }

    /// <summary>
    /// Nearest-neighbor search — document type and namespace inferred from <c>[VespaDocument]</c>.
    /// </summary>
    public static Task<VespaSearchResponse<T>> NearestNeighborSearchAsync<T>(
        this ISearchOperations ops,
        VespaTensor queryEmbedding,
        string embeddingField,
        int topK = 10,
        string? filter = null,
        string? rankProfile = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.NearestNeighborSearchAsync<T>(
            queryEmbedding, embeddingField, docType, topK, filter, rankProfile, ns, cancellationToken);
    }

    /// <summary>
    /// Nearest-neighbor search — document type inferred from <c>[VespaDocument]</c>;
    /// embedding field name resolved via <c>[VespaField(Name = "...")]</c>
    /// on the selected property (falls back to the property name).
    /// </summary>
    /// <example>
    /// <code>
    /// await client.Search.NearestNeighborSearchAsync&lt;Music&gt;(embedding, m =&gt; m.Embedding, topK: 10);
    /// </code>
    /// </example>
    public static Task<VespaSearchResponse<T>> NearestNeighborSearchAsync<T>(
        this ISearchOperations ops,
        VespaTensor queryEmbedding,
        Expression<Func<T, object?>> embeddingFieldSelector,
        int topK = 10,
        string? filter = null,
        string? rankProfile = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        var fieldName = VespaDocumentMeta.FieldName(embeddingFieldSelector);
        return ops.NearestNeighborSearchAsync<T>(
            queryEmbedding, fieldName, docType, topK, filter, rankProfile, ns, cancellationToken);
    }
}
