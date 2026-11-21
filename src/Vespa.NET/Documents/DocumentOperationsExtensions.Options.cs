using Vespa.Models;
using Vespa.Models.Attributes;

namespace Vespa.Documents;

public static partial class DocumentOperationsExtensions
{
    // Read options precede the ID so positional field-set calls remain unambiguous.
    /// <summary>GetAsync with request options and model-inferred document metadata.</summary>
    public static Task<VespaDocument<T>?> GetAsync<T>(
        this IDocumentOperations ops,
        DocumentRequestOptions? requestOptions,
        string documentId,
        string? fieldSet = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.GetAsync<T>(documentId, docType, ns, fieldSet, requestOptions: requestOptions, cancellationToken: cancellationToken);
    }

    /// <summary>GetByGroupAsync with request options and model-inferred document metadata.</summary>
    public static Task<VespaDocument<T>?> GetByGroupAsync<T>(
        this IDocumentOperations ops,
        DocumentRequestOptions? requestOptions,
        string group,
        string localId,
        string? fieldSet = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.GetByGroupAsync<T>(group, localId, docType, ns, fieldSet, requestOptions, cancellationToken);
    }

    /// <summary>GetByNumberAsync with request options and model-inferred document metadata.</summary>
    public static Task<VespaDocument<T>?> GetByNumberAsync<T>(
        this IDocumentOperations ops,
        DocumentRequestOptions? requestOptions,
        long number,
        string localId,
        string? fieldSet = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.GetByNumberAsync<T>(number, localId, docType, ns, fieldSet, requestOptions, cancellationToken);
    }

    /// <summary>Fetch multiple documents with request options and explicit document type and namespace.</summary>
    public static async Task<IReadOnlyList<VespaDocument<T>>> GetManyAsync<T>(
        this IDocumentOperations ops,
        DocumentRequestOptions? requestOptions,
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
                return await ops.GetAsync<T>(id, documentType, @namespace, requestOptions: requestOptions, cancellationToken: cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r is not null).ToList()!;
    }

    /// <summary>GetManyAsync with request options and model-inferred document metadata.</summary>
    public static Task<IReadOnlyList<VespaDocument<T>>> GetManyAsync<T>(
        this IDocumentOperations ops,
        DocumentRequestOptions? requestOptions,
        IReadOnlyList<string> documentIds,
        int maxConcurrency = 10,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.GetManyAsync<T>(requestOptions, documentIds, docType, ns, maxConcurrency, cancellationToken);
    }
}
