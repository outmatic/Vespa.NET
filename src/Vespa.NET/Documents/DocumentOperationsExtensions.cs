using Vespa.Models;
using Vespa.Models.Attributes;

namespace Vespa.Documents;

/// <summary>
/// Extension methods on <see cref="IDocumentOperations"/> that infer
/// <c>documentType</c> and <c>@namespace</c> from <c>[VespaDocument]</c> on the model type.
/// </summary>
public static class DocumentOperationsExtensions
{
    /// <summary>Insert or replace — document type and namespace inferred from <c>[VespaDocument]</c>.</summary>
    public static Task<VespaResponse> PutAsync<T>(
        this IDocumentOperations ops,
        string documentId,
        T document,
        string? condition = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.PutAsync(documentId, document, docType, ns, condition, requestOptions: null, cancellationToken: cancellationToken);
    }

    /// <summary>Get — document type and namespace inferred from <c>[VespaDocument]</c>.</summary>
    public static Task<VespaDocument<T>?> GetAsync<T>(
        this IDocumentOperations ops,
        string documentId,
        string? fieldSet = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.GetAsync<T>(documentId, docType, ns, fieldSet, requestOptions: null, cancellationToken: cancellationToken);
    }

    /// <summary>Delete — document type and namespace inferred from <c>[VespaDocument]</c>.</summary>
    public static Task<VespaResponse> DeleteAsync<T>(
        this IDocumentOperations ops,
        string documentId,
        string? condition = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.DeleteAsync(documentId, docType, ns, condition, requestOptions: null, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Field-level update with a <see cref="Dictionary{TKey,TValue}"/> —
    /// document type and namespace inferred from <c>[VespaDocument]</c>.
    /// </summary>
    public static Task<VespaResponse> UpdateFieldsAsync<T>(
        this IDocumentOperations ops,
        string documentId,
        Dictionary<string, FieldOperation> fieldOperations,
        bool createIfMissing = false,
        string? condition = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.UpdateFieldsAsync(documentId, fieldOperations, docType, ns, createIfMissing, condition, requestOptions: null, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Field-level update with a typed fluent builder —
    /// document type and namespace inferred from <c>[VespaDocument]</c>,
    /// field names resolved via <c>[VespaField(Name = "...")]</c> or the C# property name.
    /// </summary>
    /// <example>
    /// <code>
    /// await client.Documents.UpdateFieldsAsync&lt;Music&gt;("doc1", ops => ops
    ///     .Field(m => m.ArtistName, FieldOp.Assign("Taylor Swift"))
    ///     .Field(m => m.PlayCount,  FieldOp.Increment()));
    /// </code>
    /// </example>
    public static Task<VespaResponse> UpdateFieldsAsync<T>(
        this IDocumentOperations ops,
        string documentId,
        Action<TypedFieldUpdateBuilder<T>> configure,
        bool createIfMissing = false,
        string? condition = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var builder = new TypedFieldUpdateBuilder<T>();
        configure(builder);
        return ops.UpdateFieldsAsync<T>(documentId, builder.Build(), createIfMissing, condition, cancellationToken);
    }

    /// <summary>
    /// Field-level update by group — document type and namespace inferred from <c>[VespaDocument]</c>.
    /// </summary>
    public static Task<VespaResponse> UpdateFieldsByGroupAsync<T>(
        this IDocumentOperations ops,
        string group,
        string localId,
        Dictionary<string, FieldOperation> fieldOperations,
        bool createIfMissing = false,
        string? condition = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.UpdateFieldsByGroupAsync(group, localId, fieldOperations, docType, ns, createIfMissing, condition, requestOptions: null, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Field-level update by group with a typed fluent builder —
    /// document type and namespace inferred from <c>[VespaDocument]</c>,
    /// field names resolved via <c>[VespaField(Name = "...")]</c> or the C# property name.
    /// </summary>
    public static Task<VespaResponse> UpdateFieldsByGroupAsync<T>(
        this IDocumentOperations ops,
        string group,
        string localId,
        Action<TypedFieldUpdateBuilder<T>> configure,
        bool createIfMissing = false,
        string? condition = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var builder = new TypedFieldUpdateBuilder<T>();
        configure(builder);
        return ops.UpdateFieldsByGroupAsync<T>(group, localId, builder.Build(), createIfMissing, condition, cancellationToken);
    }

    /// <summary>
    /// Field-level update by number bucket — document type and namespace inferred from <c>[VespaDocument]</c>.
    /// </summary>
    public static Task<VespaResponse> UpdateFieldsByNumberAsync<T>(
        this IDocumentOperations ops,
        long number,
        string localId,
        Dictionary<string, FieldOperation> fieldOperations,
        bool createIfMissing = false,
        string? condition = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.UpdateFieldsByNumberAsync(number, localId, fieldOperations, docType, ns, createIfMissing, condition, requestOptions: null, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Field-level update by number bucket with a typed fluent builder —
    /// document type and namespace inferred from <c>[VespaDocument]</c>,
    /// field names resolved via <c>[VespaField(Name = "...")]</c> or the C# property name.
    /// </summary>
    public static Task<VespaResponse> UpdateFieldsByNumberAsync<T>(
        this IDocumentOperations ops,
        long number,
        string localId,
        Action<TypedFieldUpdateBuilder<T>> configure,
        bool createIfMissing = false,
        string? condition = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var builder = new TypedFieldUpdateBuilder<T>();
        configure(builder);
        return ops.UpdateFieldsByNumberAsync<T>(number, localId, builder.Build(), createIfMissing, condition, cancellationToken);
    }

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
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.GetByGroupAsync<T>(group, localId, docType, ns, fieldSet, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Get by number bucket — document type and namespace inferred from <c>[VespaDocument]</c>.
    /// </summary>
    public static Task<VespaDocument<T>?> GetByNumberAsync<T>(
        this IDocumentOperations ops,
        long number,
        string localId,
        string? fieldSet = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.GetByNumberAsync<T>(number, localId, docType, ns, fieldSet, cancellationToken: cancellationToken);
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
        string? fieldSet = null,
        int? wantedDocumentCount = null,
        TimeSpan? timeout = null,
        int? slices = null,
        int? sliceId = null,
        int? concurrency = null,
        long? fromTimestamp = null,
        long? toTimestamp = null,
        bool? includeRemoves = null,
        string? bucketSpace = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.VisitAsync<T>(docType, selection, cluster, ns, fieldSet, wantedDocumentCount, timeout,
            slices, sliceId, concurrency, fromTimestamp, toTimestamp, includeRemoves, bucketSpace, requestOptions: null, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Fetch multiple documents by ID in parallel. Vespa has no native batch-get endpoint,
    /// so this issues parallel <see cref="IDocumentOperations.GetAsync{T}"/> calls with
    /// bounded concurrency. Documents not found (404) are silently omitted from the result.
    /// </summary>
    public static async Task<IReadOnlyList<VespaDocument<T>>> GetManyAsync<T>(
        this IDocumentOperations ops,
        IReadOnlyList<string> documentIds,
        string documentType,
        string? @namespace = null,
        int maxConcurrency = 10,
        CancellationToken cancellationToken = default) where T : class
    {
        if (documentIds.Count == 0)
            return [];

        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = documentIds.Select(async id =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await ops.GetAsync<T>(id, documentType, @namespace, cancellationToken: cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).ToList()!;
    }

    /// <summary>
    /// Fetch multiple documents by ID in parallel — document type and namespace inferred from <c>[VespaDocument]</c>.
    /// </summary>
    public static Task<IReadOnlyList<VespaDocument<T>>> GetManyAsync<T>(
        this IDocumentOperations ops,
        IReadOnlyList<string> documentIds,
        int maxConcurrency = 10,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.GetManyAsync<T>(documentIds, docType, ns, maxConcurrency, cancellationToken);
    }
}
