using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Vespa.Models.Attributes;

namespace Vespa.Documents;

/// <summary>Builds native document selectors scoped to one document type.</summary>
public static class DocumentSelection
{
    /// <summary>Renders and freezes a predicate using the model's document type and field names.</summary>
    /// <remarks>
    /// The result matches only this exact document type, across namespaces when used in a root visit.
    /// Captured values are read now. Save the result with a continuation token to preserve the selection on manual resume.
    /// </remarks>
    public static string For<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(Expression<Func<T, bool>> selection) where T : class
    {
        ArgumentNullException.ThrowIfNull(selection);
        var (documentType, _) = VespaDocumentMeta.For<T>();
        return For(documentType, selection);
    }

    /// <summary>Renders a predicate against an explicit document type, without requiring a document attribute.</summary>
    /// <remarks>Use the same document type in the request path. Field names still come from the selected properties.</remarks>
    public static string For<
        [DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(string documentType, Expression<Func<T, bool>> selection) where T : class
    {
        ArgumentNullException.ThrowIfNull(selection);
        return $"({documentType} and ({DocumentCondition.Render(documentType, selection, escapeBackslashesAsHex: true)}))";
    }
}
