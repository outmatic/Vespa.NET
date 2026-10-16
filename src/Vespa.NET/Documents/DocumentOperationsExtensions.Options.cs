using Vespa.Models;
using Vespa.Models.Attributes;

namespace Vespa.Documents;

public static partial class DocumentOperationsExtensions
{
    // Options precede the ID so existing positional calls remain unambiguous.
    /// <summary>PutAsync with request options and model-inferred document metadata.</summary>
    public static Task<VespaResponse> PutAsync<T>(
        this IDocumentOperations ops,
        DocumentRequestOptions? requestOptions,
        string documentId,
        T document,
        string? condition = null,
        bool create = false,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.PutAsync(documentId, document, docType, ns, condition, create, requestOptions: requestOptions, cancellationToken: cancellationToken);
    }

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

    /// <summary>DeleteAsync with request options and model-inferred document metadata.</summary>
    public static Task<VespaResponse> DeleteAsync<T>(
        this IDocumentOperations ops,
        DocumentRequestOptions? requestOptions,
        string documentId,
        string? condition = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.DeleteAsync(documentId, docType, ns, condition, requestOptions: requestOptions, cancellationToken: cancellationToken);
    }

    /// <summary>UpdateFieldsAsync with request options and model-inferred document metadata.</summary>
    public static Task<VespaResponse> UpdateFieldsAsync<T>(
        this IDocumentOperations ops,
        DocumentRequestOptions? requestOptions,
        string documentId,
        Dictionary<string, FieldOperation> fieldOperations,
        bool createIfMissing = false,
        string? condition = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.UpdateFieldsAsync(documentId, fieldOperations, docType, ns, createIfMissing, condition, requestOptions: requestOptions, cancellationToken: cancellationToken);
    }

    /// <summary>UpdateFieldsAsync with request options and model-inferred document metadata.</summary>
    public static Task<VespaResponse> UpdateFieldsAsync<T>(
        this IDocumentOperations ops,
        DocumentRequestOptions? requestOptions,
        string documentId,
        Action<TypedFieldUpdateBuilder<T>> configure,
        bool createIfMissing = false,
        string? condition = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var builder = new TypedFieldUpdateBuilder<T>();
        configure(builder);
        return ops.UpdateFieldsAsync<T>(requestOptions, documentId, builder.Build(), createIfMissing, condition, cancellationToken);
    }

    /// <summary>UpdateFieldsByGroupAsync with request options and model-inferred document metadata.</summary>
    public static Task<VespaResponse> UpdateFieldsByGroupAsync<T>(
        this IDocumentOperations ops,
        DocumentRequestOptions? requestOptions,
        string group,
        string localId,
        Dictionary<string, FieldOperation> fieldOperations,
        bool createIfMissing = false,
        string? condition = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.UpdateFieldsByGroupAsync(group, localId, fieldOperations, docType, ns, createIfMissing, condition, requestOptions: requestOptions, cancellationToken: cancellationToken);
    }

    /// <summary>UpdateFieldsByGroupAsync with request options and model-inferred document metadata.</summary>
    public static Task<VespaResponse> UpdateFieldsByGroupAsync<T>(
        this IDocumentOperations ops,
        DocumentRequestOptions? requestOptions,
        string group,
        string localId,
        Action<TypedFieldUpdateBuilder<T>> configure,
        bool createIfMissing = false,
        string? condition = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var builder = new TypedFieldUpdateBuilder<T>();
        configure(builder);
        return ops.UpdateFieldsByGroupAsync<T>(requestOptions, group, localId, builder.Build(), createIfMissing, condition, cancellationToken);
    }

    /// <summary>UpdateFieldsByNumberAsync with request options and model-inferred document metadata.</summary>
    public static Task<VespaResponse> UpdateFieldsByNumberAsync<T>(
        this IDocumentOperations ops,
        DocumentRequestOptions? requestOptions,
        long number,
        string localId,
        Dictionary<string, FieldOperation> fieldOperations,
        bool createIfMissing = false,
        string? condition = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (docType, ns) = VespaDocumentMeta.For<T>();
        return ops.UpdateFieldsByNumberAsync(number, localId, fieldOperations, docType, ns, createIfMissing, condition, requestOptions: requestOptions, cancellationToken: cancellationToken);
    }

    /// <summary>UpdateFieldsByNumberAsync with request options and model-inferred document metadata.</summary>
    public static Task<VespaResponse> UpdateFieldsByNumberAsync<T>(
        this IDocumentOperations ops,
        DocumentRequestOptions? requestOptions,
        long number,
        string localId,
        Action<TypedFieldUpdateBuilder<T>> configure,
        bool createIfMissing = false,
        string? condition = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var builder = new TypedFieldUpdateBuilder<T>();
        configure(builder);
        return ops.UpdateFieldsByNumberAsync<T>(requestOptions, number, localId, builder.Build(), createIfMissing, condition, cancellationToken);
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
