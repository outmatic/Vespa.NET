using Vespa.Models;

namespace Vespa.Documents;

/// <summary>
/// Interface for document CRUD operations
/// </summary>
public interface IDocumentOperations
{
    /// <summary>
    /// Insert or update a document
    /// </summary>
    Task<VespaResponse> PutAsync<T>(
        string documentId,
        T document,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Get a document by ID
    /// </summary>
    Task<VespaDocument<T>?> GetAsync<T>(
        string documentId,
        string documentType,
        string? @namespace = null,
        string? fieldSet = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Delete a document by ID
    /// </summary>
    Task<VespaResponse> DeleteAsync(
        string documentId,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Partial update using field-level operations (assign, increment, add, etc.)
    /// </summary>
    Task<VespaResponse> UpdateFieldsAsync(
        string documentId,
        Dictionary<string, FieldOperation> fieldOperations,
        string documentType,
        string? @namespace = null,
        bool createIfMissing = false,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get a document addressed by string group name and local document ID.
    /// Maps to <c>GET /document/v1/{namespace}/{type}/group/{group}/{lid}</c>.
    /// <para>Group/number addressing is primarily meaningful for document types in
    /// <c>streaming</c> or <c>store-only</c> mode; in <c>index</c> mode Vespa computes
    /// bucket placement automatically regardless of this addressing.</para>
    /// </summary>
    Task<VespaDocument<T>?> GetByGroupAsync<T>(
        string group,
        string localId,
        string documentType,
        string? @namespace = null,
        string? fieldSet = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Get a document addressed by integer number bucket and local document ID.
    /// Maps to <c>GET /document/v1/{namespace}/{type}/number/{number}/{lid}</c>.
    /// <para>See <see cref="GetByGroupAsync{T}"/> for applicability notes.</para>
    /// </summary>
    Task<VespaDocument<T>?> GetByNumberAsync<T>(
        long number,
        string localId,
        string documentType,
        string? @namespace = null,
        string? fieldSet = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Put a document addressed by string group name and local document ID.
    /// Maps to <c>POST /document/v1/{namespace}/{type}/group/{group}/{lid}</c>.
    /// See <see cref="GetByGroupAsync{T}"/> for applicability notes.
    /// </summary>
    Task<VespaResponse> PutByGroupAsync<T>(
        string group,
        string localId,
        T document,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Put a document addressed by integer number bucket and local document ID.
    /// Maps to <c>POST /document/v1/{namespace}/{type}/number/{number}/{lid}</c>.
    /// </summary>
    Task<VespaResponse> PutByNumberAsync<T>(
        long number,
        string localId,
        T document,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    ) where T : class;

    /// <summary>
    /// Field-level partial update of a document addressed by string group name and local document ID.
    /// </summary>
    Task<VespaResponse> UpdateFieldsByGroupAsync(
        string group,
        string localId,
        Dictionary<string, FieldOperation> fieldOperations,
        string documentType,
        string? @namespace = null,
        bool createIfMissing = false,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Field-level partial update of a document addressed by integer number bucket and local document ID.
    /// </summary>
    Task<VespaResponse> UpdateFieldsByNumberAsync(
        long number,
        string localId,
        Dictionary<string, FieldOperation> fieldOperations,
        string documentType,
        string? @namespace = null,
        bool createIfMissing = false,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a document addressed by string group name and local document ID.
    /// Maps to <c>DELETE /document/v1/{namespace}/{type}/group/{group}/{lid}</c>.
    /// </summary>
    Task<VespaResponse> DeleteByGroupAsync(
        string group,
        string localId,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete a document addressed by integer number bucket and local document ID.
    /// Maps to <c>DELETE /document/v1/{namespace}/{type}/number/{number}/{lid}</c>.
    /// </summary>
    Task<VespaResponse> DeleteByNumberAsync(
        long number,
        string localId,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update all documents matching a selection expression (no docId required).
    /// Maps to <c>PUT /document/v1/{namespace}/{type}/docid/?selection=expr&amp;cluster=…</c>.
    /// Vespa processes selection ops in time-bounded chunks and returns a <c>continuation</c>
    /// token; this method automatically loops until all chunks are processed, summing
    /// <c>documentCount</c> into <see cref="VespaResponse.DocumentCount"/>.
    /// </summary>
    Task<VespaResponse> UpdateBySelectionAsync(
        string selection,
        Dictionary<string, FieldOperation> fieldOperations,
        string documentType,
        string? @namespace = null,
        string? cluster = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Delete all documents matching a selection expression (no docId required).
    /// Maps to <c>DELETE /document/v1/{namespace}/{type}/docid/?selection=expr&amp;cluster=…</c>.
    /// Loops internally on <c>continuation</c> tokens until all chunks are processed.
    /// </summary>
    Task<VespaResponse> DeleteBySelectionAsync(
        string selection,
        string documentType,
        string? @namespace = null,
        string? cluster = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Copy all documents matching a selection expression from <paramref name="cluster"/> to
    /// <paramref name="destinationCluster"/>. Maps to
    /// <c>POST /document/v1/{namespace}/{type}/docid/?selection=expr&amp;cluster=src&amp;destinationCluster=dst</c>.
    /// Loops internally on <c>continuation</c> tokens until all chunks are processed.
    /// </summary>
    /// <param name="selection">Vespa selection expression (e.g. <c>music.year &lt; 2000</c>).</param>
    /// <param name="documentType">Document type (schema name).</param>
    /// <param name="cluster">Source content cluster to read from.</param>
    /// <param name="destinationCluster">Destination content cluster to write to.</param>
    /// <param name="namespace">Vespa namespace; falls back to <c>VespaClientOptions.DefaultNamespace</c>.</param>
    /// <param name="requestOptions">Optional Vespa-level parameters (<c>timeChunk</c>, <c>bucketSpace</c>, <c>timeout</c>, <c>tracelevel</c>).</param>
    /// <param name="cancellationToken">Cancels the operation and propagates <see cref="OperationCanceledException"/>.</param>
    Task<VespaResponse> CopyBySelectionAsync(
        string selection,
        string documentType,
        string cluster,
        string destinationCluster,
        string? @namespace = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Single-chunk variant of <see cref="UpdateBySelectionAsync"/> for crash-safe manual pagination.
    /// Performs one HTTP call and returns <see cref="SelectionPageResult"/>; pass
    /// <see cref="SelectionPageResult.Continuation"/> back in the next call to resume.
    /// </summary>
    Task<SelectionPageResult> UpdateBySelectionPageAsync(
        string selection,
        Dictionary<string, FieldOperation> fieldOperations,
        string documentType,
        string? @namespace = null,
        string? cluster = null,
        string? continuation = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Single-chunk variant of <see cref="DeleteBySelectionAsync"/> for crash-safe manual pagination.
    /// </summary>
    Task<SelectionPageResult> DeleteBySelectionPageAsync(
        string selection,
        string documentType,
        string? @namespace = null,
        string? cluster = null,
        string? continuation = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Single-chunk variant of <see cref="CopyBySelectionAsync"/> for crash-safe manual pagination.
    /// </summary>
    Task<SelectionPageResult> CopyBySelectionPageAsync(
        string selection,
        string documentType,
        string cluster,
        string destinationCluster,
        string? @namespace = null,
        string? continuation = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Iterate over all documents matching an optional selection, using continuation tokens
    /// </summary>
    /// <summary>
    /// Iterate over all documents using JSONL streaming (<c>Accept: application/jsonl</c>).
    /// More efficient than <see cref="VisitAsync{T}"/> for large result sets as it streams
    /// one document per line without accumulating full pages in memory.
    /// </summary>
    IAsyncEnumerable<VespaDocument<T>> VisitJsonlAsync<T>(
        string documentType,
        string? selection = null,
        string? cluster = null,
        string? @namespace = null,
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
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    ) where T : class;

    IAsyncEnumerable<VespaDocument<T>> VisitAsync<T>(
        string documentType,
        string? selection = null,
        string? cluster = null,
        string? @namespace = null,
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
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default
    ) where T : class;
}
