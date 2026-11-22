using System.Linq.Expressions;
using Vespa.Models.Attributes;

namespace Vespa.Documents;

/// <summary>Builds native document field lists for GET and visit requests.</summary>
public static class DocumentFieldSet
{
    /// <summary>Renders a field list using the model's document type and mapped property names.</summary>
    /// <remarks>Requires at least one direct document property. Names are compared ordinally; duplicate names are removed and first-appearance order is preserved.</remarks>
    public static string For<T>(params Expression<Func<T, object?>>[] fields) where T : class
    {
        ValidateFields(fields);
        var (documentType, _) = VespaDocumentMeta.For<T>();
        return Render(documentType, fields);
    }

    /// <summary>Renders mapped properties for an explicit document type without requiring a document attribute.</summary>
    /// <remarks>The qualifier does not change the request's target or document selection. Pass the result as the named fieldSet argument or VisitOptions.FieldSet.</remarks>
    public static string ForDocumentType<T>(string documentType, params Expression<Func<T, object?>>[] fields) where T : class
    {
        ValidateFields(fields);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        return Render(documentType, fields);
    }

    private static string Render<T>(string documentType, Expression<Func<T, object?>>[] fields)
    {
        if (!IsIdentifier(documentType))
            throw new ArgumentException($"'{documentType}' is not a valid document type name for a field set.", nameof(documentType));

        List<string> names = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field, nameof(fields));
            var name = DocumentFieldSelector.FieldName(field);
            if (!IsIdentifier(name))
                throw new ArgumentException($"'{name}' is not a valid top-level document field name for a field set.", nameof(fields));
            if (seen.Add(name))
                names.Add(name);
        }
        return $"{documentType}:{string.Join(",", names)}";
    }

    private static void ValidateFields<T>(Expression<Func<T, object?>>[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Length == 0)
            throw new ArgumentException("Specify at least one document field.", nameof(fields));
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
