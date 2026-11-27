using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Vespa.Models;
using Vespa.Models.Attributes;

namespace Vespa.Documents;

public static partial class DocumentOperationsExtensions
{
    /// <summary>Updates matching documents of the model's type and namespace, following all continuation tokens.</summary>
    /// <remarks>Exactly one nonblank native selection or typed predicate is required. Typed values are frozen before the update starts.</remarks>
    public static Task<VespaResponse> UpdateBySelectionAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        Dictionary<string, FieldOperation> fieldOperations,
        Expression<Func<T, bool>>? selection = null,
        string? rawSelection = null,
        string? cluster = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(fieldOperations);
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        var selector = RequireSelection(documentType, selection, rawSelection);
        return ops.UpdateBySelectionAsync(selector, fieldOperations, documentType, ns, cluster, requestOptions, cancellationToken);
    }

    /// <summary>Configures a typed update and applies it to all matching documents of the model's type and namespace.</summary>
    /// <remarks>The required selection is validated and frozen before the callback runs.</remarks>
    public static Task<VespaResponse> UpdateBySelectionAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        Action<TypedFieldUpdateBuilder<T>> configure,
        Expression<Func<T, bool>>? selection = null,
        string? rawSelection = null,
        string? cluster = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        var selector = RequireSelection(documentType, selection, rawSelection);
        var builder = new TypedFieldUpdateBuilder<T>();
        configure(builder);
        return ops.UpdateBySelectionAsync(selector, builder.Build(), documentType, ns, cluster, requestOptions, cancellationToken);
    }

    /// <summary>Updates one chunk of matching documents and returns its count and continuation token.</summary>
    /// <remarks>Exactly one selection is required. Resume with the same selector, cluster and request options.</remarks>
    public static Task<SelectionPageResult> UpdateBySelectionPageAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        Dictionary<string, FieldOperation> fieldOperations,
        Expression<Func<T, bool>>? selection = null,
        string? rawSelection = null,
        string? cluster = null,
        string? continuation = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(fieldOperations);
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        var selector = RequireSelection(documentType, selection, rawSelection);
        return ops.UpdateBySelectionPageAsync(selector, fieldOperations, documentType, ns, cluster, continuation, requestOptions, cancellationToken);
    }

    /// <summary>Configures a typed field update for one selection chunk.</summary>
    /// <remarks>Validates and freezes the required selection before the callback. For manual resume, save a selector rendered by DocumentSelection.For and pass it through rawSelection on every call.</remarks>
    public static Task<SelectionPageResult> UpdateBySelectionPageAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        Action<TypedFieldUpdateBuilder<T>> configure,
        Expression<Func<T, bool>>? selection = null,
        string? rawSelection = null,
        string? cluster = null,
        string? continuation = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        var selector = RequireSelection(documentType, selection, rawSelection);
        var builder = new TypedFieldUpdateBuilder<T>();
        configure(builder);
        return ops.UpdateBySelectionPageAsync(selector, builder.Build(), documentType, ns, cluster, continuation, requestOptions, cancellationToken);
    }

    /// <summary>Deletes all matching documents of the model's type and namespace.</summary>
    /// <remarks>Exactly one selection is required. Use an explicit true predicate to select the entire addressed type.</remarks>
    public static Task<VespaResponse> DeleteBySelectionAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        Expression<Func<T, bool>>? selection = null,
        string? rawSelection = null,
        string? cluster = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        var selector = RequireSelection(documentType, selection, rawSelection);
        return ops.DeleteBySelectionAsync(selector, documentType, ns, cluster, requestOptions, cancellationToken);
    }

    /// <summary>Deletes one selection chunk and returns its count and continuation token.</summary>
    /// <remarks>Exactly one selection is required. Resume with the same selector, cluster and request options.</remarks>
    public static Task<SelectionPageResult> DeleteBySelectionPageAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        Expression<Func<T, bool>>? selection = null,
        string? rawSelection = null,
        string? cluster = null,
        string? continuation = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        var selector = RequireSelection(documentType, selection, rawSelection);
        return ops.DeleteBySelectionPageAsync(selector, documentType, ns, cluster, continuation, requestOptions, cancellationToken);
    }

    /// <summary>Copies matching documents of the model's type and namespace between content clusters.</summary>
    /// <remarks>The source documents remain in place. Exactly one selection and both cluster names are required.</remarks>
    public static Task<VespaResponse> CopyBySelectionAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        string cluster,
        string destinationCluster,
        Expression<Func<T, bool>>? selection = null,
        string? rawSelection = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cluster);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationCluster);
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        var selector = RequireSelection(documentType, selection, rawSelection);
        return ops.CopyBySelectionAsync(selector, documentType, cluster, destinationCluster, ns, requestOptions, cancellationToken);
    }

    /// <summary>Copies one selection chunk between content clusters and returns its count and continuation token.</summary>
    /// <remarks>Exactly one selection and both cluster names are required. Resume with the same selector and cluster names.</remarks>
    public static Task<SelectionPageResult> CopyBySelectionPageAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        string cluster,
        string destinationCluster,
        Expression<Func<T, bool>>? selection = null,
        string? rawSelection = null,
        string? continuation = null,
        SelectionRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cluster);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationCluster);
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        var selector = RequireSelection(documentType, selection, rawSelection);
        return ops.CopyBySelectionPageAsync(selector, documentType, cluster, destinationCluster, ns, continuation, requestOptions, cancellationToken);
    }

    private static string RequireSelection<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(string documentType, Expression<Func<T, bool>>? selection, string? rawSelection) where T : class
    {
        if (selection is not null && rawSelection is not null)
            throw new ArgumentException("Specify either selection or rawSelection, not both.", nameof(rawSelection));
        if (selection is not null)
            return DocumentSelection.For(documentType, selection);
        if (string.IsNullOrWhiteSpace(rawSelection))
            throw new ArgumentException("A selection is required. Use an explicit true predicate or a nonblank native selector.", nameof(rawSelection));
        return rawSelection;
    }
}
