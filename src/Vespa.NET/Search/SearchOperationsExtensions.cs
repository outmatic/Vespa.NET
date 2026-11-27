using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Models.Tensors;

namespace Vespa.Search;

/// <summary>
/// Extension methods on <see cref="ISearchOperations"/> that infer
/// <c>documentType</c> from <c>[VespaDocument]</c> on the model type.
/// </summary>
public static class SearchOperationsExtensions
{
    /// <summary>
    /// Streams group-list pages for a single grouping expression. Pass the expression
    /// in the YQL pipe or in <see cref="VespaSearchRequest.Select"/>.
    /// Each yielded response contains one page of grouped results.
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
        ArgumentNullException.ThrowIfNull(ops);
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<string>? tokens = null;
        while (true)
        {
            var pageRequest = tokens is null ? request : GroupingContinuations.ApplyToRequest(request, tokens);

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
    /// Starts at the request offset and uses <paramref name="pageSize"/> for each request.
    /// Stops automatically when a page returns fewer hits than <paramref name="pageSize"/>.
    /// Use a stable order and omit inline YQL pagination so Vespa uses the request values.
    /// <para>
    /// <b>Server limits:</b> Vespa caps <c>hits</c> at <c>maxHits</c> (default 400) and
    /// <c>offset</c> at <c>maxOffset</c> (default 1000) — deep pagination fails unless those
    /// query-profile limits are raised. To export a full corpus, use document visiting
    /// (<c>Documents.VisitAsync</c>/<c>VisitJsonlAsync</c>) instead.
    /// </para>
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
            var pageRequest = request with { Hits = pageSize, Offset = offset };
            var page = await ops.SearchAsync<T>(pageRequest, cancellationToken);
            yield return page;
            returned = page.Root.Children.Count;
            offset += returned;
        }
        while (returned == pageSize);
    }

    /// <summary>
    /// Nearest-neighbor search — document type inferred from <c>[VespaDocument]</c>.
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
        var (docType, _) = VespaDocumentMeta.For<T>();
        return ops.NearestNeighborSearchAsync<T>(
            queryEmbedding, embeddingField, docType, topK, filter, rankProfile, cancellationToken);
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
        var (docType, _) = VespaDocumentMeta.For<T>();
        var fieldName = VespaDocumentMeta.FieldName(embeddingFieldSelector);
        return ops.NearestNeighborSearchAsync<T>(
            queryEmbedding, fieldName, docType, topK, filter, rankProfile, cancellationToken);
    }
}
