using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Vespa.Models;
using Vespa.Models.Attributes;

namespace Vespa.Documents;

public static partial class DocumentOperationsExtensions
{
    /// <summary>Visits a model's document type and namespace with an optional typed or native selection.</summary>
    /// <remarks>Typed selections are rendered before enumeration and kept unchanged across continuation requests.</remarks>
    public static IAsyncEnumerable<VespaDocument<T>> VisitAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        Expression<Func<T, bool>>? selection = null,
        string? rawSelection = null,
        string? cluster = null,
        VisitOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        var selector = ResolveSelection(documentType, selection, rawSelection);
        return ops.VisitAsync<T>(documentType, selector, cluster, ns, options, cancellationToken);
    }

    /// <summary>Visits one page of a model's document type and namespace with an optional typed or native selection.</summary>
    /// <remarks>Resume with the same selection, cluster and options. Captured values are read on each call.</remarks>
    public static Task<VespaVisitResponse<T>> VisitPageAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        Expression<Func<T, bool>>? selection = null,
        string? rawSelection = null,
        string? cluster = null,
        string? continuation = null,
        VisitOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        var selector = ResolveSelection(documentType, selection, rawSelection);
        return ops.VisitPageAsync<T>(documentType, selector, cluster, ns, continuation, options, cancellationToken);
    }

    /// <summary>Streams a model's documents as JSONL with an optional typed or native selection.</summary>
    /// <remarks>Requires Vespa 8.593 or newer. Typed selections are frozen before enumeration.</remarks>
    public static IAsyncEnumerable<VespaDocument<T>> VisitJsonlAsync<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        this IDocumentOperations ops,
        Expression<Func<T, bool>>? selection = null,
        string? rawSelection = null,
        string? cluster = null,
        VisitOptions? options = null,
        CancellationToken cancellationToken = default) where T : class
    {
        var (documentType, ns) = VespaDocumentMeta.For<T>();
        var selector = ResolveSelection(documentType, selection, rawSelection);
        return ops.VisitJsonlAsync<T>(documentType, selector, cluster, ns, options, cancellationToken);
    }

    /// <summary>Visits a filter model's exact document type across namespaces, deserializing a separate result model.</summary>
    /// <remarks>The result model needs no document attribute. The selection is frozen before enumeration.</remarks>
    public static IAsyncEnumerable<VespaDocument<TResult>> VisitAllAsync<TResult,
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] TFilter>(
        this IDocumentOperations ops,
        string cluster,
        Expression<Func<TFilter, bool>> selection,
        VisitOptions? options = null,
        CancellationToken cancellationToken = default) where TResult : class where TFilter : class =>
        ops.VisitAllAsync<TResult>(cluster, DocumentSelection.For(selection), options, cancellationToken);

    /// <summary>Visits one root page of a filter model's exact document type, deserializing a separate result model.</summary>
    /// <remarks>Resume with the same selection, cluster and options. Captured values are read on each call.</remarks>
    public static Task<VespaVisitResponse<TResult>> VisitAllPageAsync<TResult,
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] TFilter>(
        this IDocumentOperations ops,
        string cluster,
        Expression<Func<TFilter, bool>> selection,
        string? continuation = null,
        VisitOptions? options = null,
        CancellationToken cancellationToken = default) where TResult : class where TFilter : class =>
        ops.VisitAllPageAsync<TResult>(cluster, DocumentSelection.For(selection), continuation, options, cancellationToken);

    private static string? ResolveSelection<[DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        string documentType, Expression<Func<T, bool>>? selection, string? rawSelection) where T : class
    {
        if (selection is not null && rawSelection is not null)
            throw new ArgumentException("Specify either selection or rawSelection, not both.", nameof(rawSelection));
        return selection is null ? rawSelection : DocumentSelection.For(documentType, selection);
    }
}
