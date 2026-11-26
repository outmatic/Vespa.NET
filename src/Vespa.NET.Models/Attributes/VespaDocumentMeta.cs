using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Vespa.Models.Schema;

namespace Vespa.Models.Attributes;

/// <summary>
/// Reads and caches Vespa metadata declared with <see cref="VespaDocumentAttribute"/>
/// and <see cref="VespaFieldAttribute"/>.
/// </summary>
public static class VespaDocumentMeta
{
    private static readonly ConcurrentDictionary<Type, (string DocumentType, string? Namespace)> _typeCache = new();
    private static readonly ConcurrentDictionary<MemberInfo, string> _fieldCache = new();

    /// <summary>
    /// Returns <c>(DocumentType, Namespace)</c> declared on <typeparamref name="T"/> via
    /// <see cref="VespaDocumentAttribute"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <typeparamref name="T"/> is not decorated with <see cref="VespaDocumentAttribute"/>.
    /// </exception>
    public static (string DocumentType, string? Namespace) For<T>()
        where T : class => For(typeof(T));

    /// <inheritdoc cref="For{T}"/>
    public static (string DocumentType, string? Namespace) For(Type type) =>
        _typeCache.GetOrAdd(type, static t =>
        {
            var attr = t.GetCustomAttribute<VespaDocumentAttribute>()
                ?? throw new InvalidOperationException(
                    $"Type '{t.Name}' must be decorated with [VespaDocument(\"...\")].");
            return (attr.DocumentType, attr.Namespace);
        });

    /// <summary>
    /// Resolves the Vespa field name for the property selected by <paramref name="selector"/>.
    /// Uses <c>[VespaField(Name = "...")]</c> when present; otherwise converts the property name to snake_case.
    /// The selector must access a public, readable instance property directly on its parameter.
    /// JSON serialization exclusions are not applied here: a query may reference a field populated by another writer.
    /// </summary>
    /// <example>
    /// <code>
    /// VespaDocumentMeta.FieldName&lt;Music&gt;(m =&gt; m.ArtistName)
    /// // → "artist_name"  (when [VespaField(Name = "artist_name")] is present)
    /// // → "artist_name"  (snake_case fallback)
    /// </code>
    /// </example>
    public static string FieldName<T>(Expression<Func<T, object?>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var body = selector.Body;
        // Only strip the ordinary conversion to object used for property selectors.
        // Numeric conversions, checked casts and user-defined operators change the selected value.
        if (body is UnaryExpression { NodeType: ExpressionType.Convert, Method: null } convert && convert.Type == typeof(object))
            body = convert.Operand;

        if (body is not MemberExpression { Member: PropertyInfo property, Expression: var target } ||
            target != selector.Parameters[0] ||
            property.GetMethod is not { IsPublic: true, IsStatic: false } ||
            property.GetIndexParameters().Length != 0)
            throw new ArgumentException(
                "Expression must select a public, readable instance property directly, e.g. m => m.FieldName. Use an explicit field path for nested fields.",
                nameof(selector));

        return _fieldCache.GetOrAdd(property, static member =>
        {
            var field = member.GetCustomAttribute<VespaFieldAttribute>();
            if (field?.Name is not null)
                return field.Name;

            // Match JSON serialization convention: snake_case the property name
            return VespaNaming.ToSnakeCase(member.Name);
        });
    }
}
