using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Vespa.Models;
using Vespa.Models.Attributes;

namespace Vespa.Documents;

public static partial class DocumentOperationsExtensions
{
    /// <summary>Insert or replace, optionally checking the stored document with a typed or native condition.</summary>
    /// <remarks>With <paramref name="create"/>, an absent document is created without evaluating the condition (Vespa ≥ 8.178).</remarks>
    public static Task<VespaResponse> PutAsync<[DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        string documentId,
        T document,
        Expression<Func<T, bool>>? condition = null,
        string? rawCondition = null,
        bool create = false,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        return ops.PutAsync(documentId, document, documentType, ns, ResolveCondition(condition, rawCondition), create, requestOptions, cancellationToken);
    }

    /// <summary>Delete, optionally checking the stored document with a typed or native condition.</summary>
    public static Task<VespaResponse> DeleteAsync<[DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        string documentId,
        Expression<Func<T, bool>>? condition = null,
        string? rawCondition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        return ops.DeleteAsync(documentId, documentType, ns, ResolveCondition(condition, rawCondition), requestOptions, cancellationToken);
    }

    /// <summary>Update fields, optionally checking the stored document with a typed or native condition.</summary>
    public static Task<VespaResponse> UpdateFieldsAsync<[DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        string documentId,
        Dictionary<string, FieldOperation> fieldOperations,
        Expression<Func<T, bool>>? condition = null,
        string? rawCondition = null,
        bool createIfMissing = false,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        return ops.UpdateFieldsAsync(documentId, fieldOperations, documentType, ns, createIfMissing, ResolveCondition(condition, rawCondition), requestOptions, cancellationToken);
    }

    /// <summary>Configure a typed field update with an optional typed or native condition on the stored document.</summary>
    public static Task<VespaResponse> UpdateFieldsAsync<[DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        string documentId,
        Action<TypedFieldUpdateBuilder<T>> configure,
        Expression<Func<T, bool>>? condition = null,
        string? rawCondition = null,
        bool createIfMissing = false,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        var selector = ResolveCondition(condition, rawCondition);
        var builder = new TypedFieldUpdateBuilder<T>();
        configure(builder);
        return ops.UpdateFieldsAsync<T>(documentId, builder.Build(), createIfMissing: createIfMissing, rawCondition: selector, requestOptions: requestOptions, cancellationToken: cancellationToken);
    }

    /// <summary>Put by group with an optional typed or native condition on the stored document.</summary>
    public static Task<VespaResponse> PutByGroupAsync<[DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        string group,
        string localId,
        T document,
        Expression<Func<T, bool>>? condition = null,
        string? rawCondition = null,
        bool create = false,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        return ops.PutByGroupAsync(group, localId, document, documentType, ns, ResolveCondition(condition, rawCondition), create, requestOptions, cancellationToken);
    }

    /// <summary>Put by number bucket with an optional typed or native condition on the stored document.</summary>
    public static Task<VespaResponse> PutByNumberAsync<[DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        long number,
        string localId,
        T document,
        Expression<Func<T, bool>>? condition = null,
        string? rawCondition = null,
        bool create = false,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        return ops.PutByNumberAsync(number, localId, document, documentType, ns, ResolveCondition(condition, rawCondition), create, requestOptions, cancellationToken);
    }

    /// <summary>Delete by group with an optional typed or native condition on the stored document.</summary>
    public static Task<VespaResponse> DeleteByGroupAsync<[DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        string group,
        string localId,
        Expression<Func<T, bool>>? condition = null,
        string? rawCondition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        return ops.DeleteByGroupAsync(group, localId, documentType, ns, ResolveCondition(condition, rawCondition), requestOptions, cancellationToken);
    }

    /// <summary>Delete by number bucket with an optional typed or native condition on the stored document.</summary>
    public static Task<VespaResponse> DeleteByNumberAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        long number,
        string localId,
        Expression<Func<T, bool>>? condition = null,
        string? rawCondition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        return ops.DeleteByNumberAsync(number, localId, documentType, ns, ResolveCondition(condition, rawCondition), requestOptions, cancellationToken);
    }

    /// <summary>Update fields by group with an optional typed or native condition on the stored document.</summary>
    public static Task<VespaResponse> UpdateFieldsByGroupAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        string group,
        string localId,
        Dictionary<string, FieldOperation> fieldOperations,
        Expression<Func<T, bool>>? condition = null,
        string? rawCondition = null,
        bool createIfMissing = false,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        return ops.UpdateFieldsByGroupAsync(group, localId, fieldOperations, documentType, ns, createIfMissing, ResolveCondition(condition, rawCondition), requestOptions, cancellationToken);
    }

    /// <summary>Configure a typed field update by group with an optional typed or native condition.</summary>
    public static Task<VespaResponse> UpdateFieldsByGroupAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        string group,
        string localId,
        Action<TypedFieldUpdateBuilder<T>> configure,
        Expression<Func<T, bool>>? condition = null,
        string? rawCondition = null,
        bool createIfMissing = false,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        var selector = ResolveCondition(condition, rawCondition);
        var builder = new TypedFieldUpdateBuilder<T>();
        configure(builder);
        return ops.UpdateFieldsByGroupAsync<T>(group, localId, builder.Build(), createIfMissing: createIfMissing, rawCondition: selector, requestOptions: requestOptions, cancellationToken: cancellationToken);
    }

    /// <summary>Update fields by number bucket with an optional typed or native condition on the stored document.</summary>
    public static Task<VespaResponse> UpdateFieldsByNumberAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        long number,
        string localId,
        Dictionary<string, FieldOperation> fieldOperations,
        Expression<Func<T, bool>>? condition = null,
        string? rawCondition = null,
        bool createIfMissing = false,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        return ops.UpdateFieldsByNumberAsync(number, localId, fieldOperations, documentType, ns, createIfMissing, ResolveCondition(condition, rawCondition), requestOptions, cancellationToken);
    }

    /// <summary>Configure a typed field update by number bucket with an optional typed or native condition.</summary>
    public static Task<VespaResponse> UpdateFieldsByNumberAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        long number,
        string localId,
        Action<TypedFieldUpdateBuilder<T>> configure,
        Expression<Func<T, bool>>? condition = null,
        string? rawCondition = null,
        bool createIfMissing = false,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        var selector = ResolveCondition(condition, rawCondition);
        var builder = new TypedFieldUpdateBuilder<T>();
        configure(builder);
        return ops.UpdateFieldsByNumberAsync<T>(number, localId, builder.Build(), createIfMissing: createIfMissing, rawCondition: selector, requestOptions: requestOptions, cancellationToken: cancellationToken);
    }

    private static string? ResolveCondition<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(Expression<Func<T, bool>>? condition, string? rawCondition) where T : class
    {
        if (condition is not null && rawCondition is not null)
            throw new ArgumentException("Specify either condition or rawCondition, not both.", nameof(rawCondition));
        return condition is null ? rawCondition : DocumentCondition.For(condition);
    }
}
