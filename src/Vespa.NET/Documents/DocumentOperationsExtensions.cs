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
