using Vespa.Documents;
using Vespa.Models;
using Vespa.Models.Attributes;

namespace Vespa.Feed;

/// <summary>
/// One put, field update or delete in a mixed feed. Factories infer schema and
/// namespace from <c>[VespaDocument]</c> unless an explicit document type is supplied.
/// Payload objects must not be mutated until the feed completes.
/// </summary>
public abstract class FeedOperation
{
    private FeedOperation(FeedOperationKind kind, string documentId, string documentType,
        string? @namespace, string? condition, DocumentRequestOptions? requestOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        if (@namespace is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);

        Kind = kind;
        Id = documentId;
        DocumentType = documentType;
        Namespace = @namespace;
        Condition = condition;
        RequestOptions = requestOptions;
    }

    /// <summary>The mutation to perform.</summary>
    public FeedOperationKind Kind { get; }

    /// <summary>Bare user ID or full Vespa document ID, including group/number addressing.</summary>
    public string Id { get; }

    /// <summary>The target document type (schema).</summary>
    public string DocumentType { get; }

    /// <summary>Target namespace; null uses the client's default namespace.</summary>
    public string? Namespace { get; }

    /// <summary>Optional Vespa test-and-set condition.</summary>
    public string? Condition { get; }

    /// <summary>Routing, timeout and other options for this operation.</summary>
    public DocumentRequestOptions? RequestOptions { get; }

    /// <summary>
    /// Create an insert/replace operation. An explicit <paramref name="documentType"/>
    /// allows models without <c>[VespaDocument]</c>; its namespace defaults to the client.
    /// </summary>
    public static FeedOperation Put<T>(
        string documentId,
        T document,
        string? documentType = null,
        string? @namespace = null,
        string? condition = null,
        bool create = false,
        DocumentRequestOptions? requestOptions = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(document);
        var (type, ns) = ResolveTarget<T>(documentType, @namespace);
        return new PutOperation<T>(documentId, document, type, ns, condition, create, requestOptions);
    }

    /// <summary>Create a model-aware field update using property selectors.</summary>
    public static FeedOperation Update<T>(
        string documentId,
        Action<TypedFieldUpdateBuilder<T>> configure,
        string? documentType = null,
        string? @namespace = null,
        bool createIfMissing = false,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new TypedFieldUpdateBuilder<T>();
        configure(builder);
        return Update<T>(documentId, builder.Build(), documentType, @namespace, createIfMissing, condition, requestOptions);
    }

    /// <summary>Create a model-aware field update using Vespa field names and operations.</summary>
    public static FeedOperation Update<T>(
        string documentId,
        IReadOnlyDictionary<string, FieldOperation> fieldOperations,
        string? documentType = null,
        string? @namespace = null,
        bool createIfMissing = false,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(fieldOperations);
        var (type, ns) = ResolveTarget<T>(documentType, @namespace);
        return Update(documentId, fieldOperations, type, ns, createIfMissing, condition, requestOptions);
    }

    /// <summary>Create a field update with an explicit schema and optional namespace.</summary>
    public static FeedOperation Update(
        string documentId,
        IReadOnlyDictionary<string, FieldOperation> fieldOperations,
        string documentType,
        string? @namespace = null,
        bool createIfMissing = false,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null)
    {
        ArgumentNullException.ThrowIfNull(fieldOperations);
        return new UpdateOperation(documentId, new(fieldOperations), documentType, @namespace,
            createIfMissing, condition, requestOptions);
    }

    /// <summary>Create a model-aware delete operation.</summary>
    public static FeedOperation Delete<T>(
        string documentId,
        string? documentType = null,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null) where T : class
    {
        var (type, ns) = ResolveTarget<T>(documentType, @namespace);
        return Delete(documentId, type, ns, condition, requestOptions);
    }

    /// <summary>Create a delete operation with an explicit schema and optional namespace.</summary>
    public static FeedOperation Delete(
        string documentId,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null) =>
        new DeleteOperation(documentId, documentType, @namespace, condition, requestOptions);

    internal abstract Task<VespaResponse> ExecuteAsync(IDocumentOperations documents, string defaultNamespace, CancellationToken cancellationToken);

    internal DocumentKey GetDocumentKey(string defaultNamespace)
    {
        var ns = Namespace ?? defaultNamespace;
        return VespaDocumentId.TryParse(Id, out var parts)
            ? new(ns, DocumentType, parts.Group, parts.Number, parts.UserSpecified)
            : new(ns, DocumentType, null, null, VespaDocumentId.GetUserSpecified(Id));
    }

    private static (string DocumentType, string? Namespace) ResolveTarget<T>(string? documentType, string? @namespace) where T : class
    {
        if (documentType is not null)
            return (documentType, @namespace);

        var (type, ns) = VespaDocumentMeta.For<T>();
        return (type, @namespace ?? ns);
    }

    private sealed class PutOperation<T>(string id, T document, string documentType,
        string? ns, string? condition, bool create, DocumentRequestOptions? requestOptions)
        : FeedOperation(FeedOperationKind.Put, id, documentType, ns, condition, requestOptions) where T : class
    {
        internal override Task<VespaResponse> ExecuteAsync(IDocumentOperations documents, string defaultNamespace, CancellationToken cancellationToken) =>
            documents.PutAsync(Id, document, DocumentType, Namespace ?? defaultNamespace, Condition, create, RequestOptions, cancellationToken);
    }

    private sealed class UpdateOperation(string id, Dictionary<string, FieldOperation> fields,
        string documentType, string? ns, bool createIfMissing, string? condition, DocumentRequestOptions? requestOptions)
        : FeedOperation(FeedOperationKind.Update, id, documentType, ns, condition, requestOptions)
    {
        internal override Task<VespaResponse> ExecuteAsync(IDocumentOperations documents, string defaultNamespace, CancellationToken cancellationToken) =>
            documents.UpdateFieldsAsync(Id, fields, DocumentType, Namespace ?? defaultNamespace, createIfMissing, Condition, RequestOptions, cancellationToken);
    }

    private sealed class DeleteOperation(string id, string documentType, string? ns,
        string? condition, DocumentRequestOptions? requestOptions)
        : FeedOperation(FeedOperationKind.Delete, id, documentType, ns, condition, requestOptions)
    {
        internal override Task<VespaResponse> ExecuteAsync(IDocumentOperations documents, string defaultNamespace, CancellationToken cancellationToken) =>
            documents.DeleteAsync(Id, DocumentType, Namespace ?? defaultNamespace, Condition, RequestOptions, cancellationToken);
    }

    internal readonly record struct DocumentKey(string Namespace, string DocumentType, string? Group, long? Number, string UserSpecified);
}
