using Vespa.Models;
using Vespa.Models.Attributes;

namespace Vespa.Documents;

/// <summary>
/// Extension methods on <see cref="IDocumentOperations"/> that infer
/// <c>documentType</c> and <c>@namespace</c> from <c>[VespaDocument]</c> on the model type.
/// </summary>
public static partial class DocumentOperationsExtensions
{
    /// <summary>Get — document type and namespace inferred from <c>[VespaDocument]</c>.</summary>
    public static Task<VespaDocument<T>?> GetAsync<T>(
        this IDocumentOperations ops,
        string documentId,
        string? fieldSet = null,
        CancellationToken cancellationToken = default) where T : class =>
        GetAsync<T>(ops, null, documentId, fieldSet, cancellationToken);

    /// <summary>
    /// Selection-based bulk update — document type and namespace inferred from <c>[VespaDocument]</c>.
    /// Loops internally on Vespa's <c>continuation</c> token until all chunks are processed.
    /// </summary>
    public static Task<VespaResponse> UpdateBySelectionAsync<T>(
        this IDocumentOperations ops,
        string selection,
        Dictionary<string, FieldOperation> fieldOperations,
        string? cluster = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.UpdateBySelectionAsync(selection, fieldOperations, docType, ns, cluster, requestOptions, cancellationToken);
    }

    /// <summary>
    /// Selection-based bulk update with a typed fluent builder —
    /// document type and namespace inferred from <c>[VespaDocument]</c>,
    /// field names resolved via <c>[VespaField(Name = "...")]</c> or the C# property name.
    /// Loops internally on Vespa's <c>continuation</c> token until all chunks are processed.
    /// </summary>
    /// <example>
    /// <code>
    /// await client.Documents.UpdateBySelectionAsync&lt;Music&gt;(
    ///     "music.year &lt; 2000",
    ///     ops => ops.Field(m => m.Status, FieldOp.Assign("archived")),
    ///     cluster: "content");
    /// </code>
    /// </example>
    public static Task<VespaResponse> UpdateBySelectionAsync<T>(
        this IDocumentOperations ops,
        string selection,
        Action<TypedFieldUpdateBuilder<T>> configure,
        string? cluster = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var builder = new TypedFieldUpdateBuilder<T>();
        configure(builder);
        return ops.UpdateBySelectionAsync<T>(selection, builder.Build(), cluster, requestOptions, cancellationToken);
    }

    /// <summary>
    /// Single-chunk variant of <see cref="UpdateBySelectionAsync{T}(IDocumentOperations,string,Dictionary{string,FieldOperation},string?,SelectionRequestOptions?,CancellationToken)"/>
    /// for crash-safe manual pagination — document type and namespace inferred from <c>[VespaDocument]</c>.
    /// </summary>
    public static Task<SelectionPageResult> UpdateBySelectionPageAsync<T>(
        this IDocumentOperations ops,
        string selection,
        Dictionary<string, FieldOperation> fieldOperations,
        string? cluster = null,
        string? continuation = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.UpdateBySelectionPageAsync(selection, fieldOperations, docType, ns, cluster, continuation, requestOptions, cancellationToken);
    }

    /// <summary>
    /// Single-chunk variant of selection-based update with a typed fluent builder —
    /// document type and namespace inferred from <c>[VespaDocument]</c>,
    /// field names resolved via <c>[VespaField(Name = "...")]</c> or the C# property name.
    /// </summary>
    public static Task<SelectionPageResult> UpdateBySelectionPageAsync<T>(
        this IDocumentOperations ops,
        string selection,
        Action<TypedFieldUpdateBuilder<T>> configure,
        string? cluster = null,
        string? continuation = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var builder = new TypedFieldUpdateBuilder<T>();
        configure(builder);
        return ops.UpdateBySelectionPageAsync<T>(selection, builder.Build(), cluster, continuation, requestOptions, cancellationToken);
    }

    /// <summary>
    /// Get by group — document type and namespace inferred from <c>[VespaDocument]</c>.
    /// </summary>
    public static Task<VespaDocument<T>?> GetByGroupAsync<T>(
        this IDocumentOperations ops,
        string group,
        string localId,
        string? fieldSet = null,
        CancellationToken cancellationToken = default) where T : class =>
        GetByGroupAsync<T>(ops, null, group, localId, fieldSet, cancellationToken);

    /// <summary>
    /// Get by number bucket — document type and namespace inferred from <c>[VespaDocument]</c>.
    /// </summary>
    public static Task<VespaDocument<T>?> GetByNumberAsync<T>(
        this IDocumentOperations ops,
        long number,
        string localId,
        string? fieldSet = null,
        CancellationToken cancellationToken = default) where T : class =>
        GetByNumberAsync<T>(ops, null, number, localId, fieldSet, cancellationToken);

    /// <summary>
    /// Visit one page — document type and namespace inferred from <c>[VespaDocument]</c>.
    /// Pass the returned continuation token back with the same selection, cluster
    /// and visit options to resume after processing the page.
    /// </summary>
    public static Task<VespaVisitResponse<T>> VisitPageAsync<T>(
        this IDocumentOperations ops,
        string? selection = null,
        string? cluster = null,
        string? continuation = null,
        VisitOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.VisitPageAsync<T>(docType, selection, cluster, ns, continuation, options, cancellationToken);
    }

    /// <summary>
    /// Visit all documents — document type and namespace inferred from <c>[VespaDocument]</c>.
    /// Use named arguments to pass <paramref name="selection"/>, e.g.
    /// <c>VisitAsync&lt;Music&gt;(selection: "music.year &gt; 2000")</c>.
    /// </summary>
    public static IAsyncEnumerable<VespaDocument<T>> VisitAsync<T>(
        this IDocumentOperations ops,
        string? selection = null,
        string? cluster = null,
        VisitOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.VisitAsync<T>(docType, selection, cluster, ns, options, cancellationToken);
    }

    /// <summary>
    /// Visit all documents via JSONL streaming — document type and namespace inferred
    /// from <c>[VespaDocument]</c>. See <see cref="IDocumentOperations.VisitJsonlAsync{T}"/>.
    /// </summary>
    public static IAsyncEnumerable<VespaDocument<T>> VisitJsonlAsync<T>(
        this IDocumentOperations ops,
        string? selection = null,
        string? cluster = null,
        VisitOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.VisitJsonlAsync<T>(docType, selection, cluster, ns, options, cancellationToken);
    }

    /// <summary>
    /// Fetch multiple documents by ID in parallel. Vespa has no native batch-get endpoint,
    /// so this issues parallel <see cref="IDocumentOperations.GetAsync{T}"/> calls with
    /// bounded concurrency. Documents not found (404) are silently omitted from the result.
    /// </summary>
    public static Task<IReadOnlyList<VespaDocument<T>>> GetManyAsync<T>(
        this IDocumentOperations ops,
        IReadOnlyList<string> documentIds,
        string documentType,
        string? @namespace = null,
        int maxConcurrency = 10,
        CancellationToken cancellationToken = default) where T : class =>
        GetManyAsync<T>(ops, null, documentIds, documentType, @namespace, maxConcurrency, cancellationToken);

    /// <summary>
    /// Fetch multiple documents by ID in parallel — document type and namespace inferred from <c>[VespaDocument]</c>.
    /// </summary>
    public static Task<IReadOnlyList<VespaDocument<T>>> GetManyAsync<T>(
        this IDocumentOperations ops,
        IReadOnlyList<string> documentIds,
        int maxConcurrency = 10,
        CancellationToken cancellationToken = default) where T : class =>
        GetManyAsync<T>(ops, null, documentIds, maxConcurrency, cancellationToken);
}
