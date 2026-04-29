using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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
    public static (string DocumentType, string? Namespace) For<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>()
        where T : class => For(typeof(T));

    /// <inheritdoc cref="For{T}"/>
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Callers are annotated with DynamicallyAccessedMembers(All).")]
    public static (string DocumentType, string? Namespace) For(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type) =>
        _typeCache.GetOrAdd(type, static t =>
        {
            var attr = t.GetCustomAttribute<VespaDocumentAttribute>()
                ?? throw new InvalidOperationException(
                    $"Type '{t.Name}' must be decorated with [VespaDocument(\"...\")].");
            return (attr.DocumentType, attr.Namespace);
        });

    /// <summary>
    /// Resolves the Vespa field name for the property selected by <paramref name="selector"/>.
    /// Uses <c>[VespaField(Name = "...")]</c> when present; falls back to the C# property name.
    /// For tensor fields, uses the property name from <c>[VespaTensor]</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// VespaDocumentMeta.FieldName&lt;Music&gt;(m =&gt; m.ArtistName)
    /// // → "artist_name"  (when [VespaField(Name = "artist_name")] is present)
    /// // → "ArtistName"   (otherwise)
    /// </code>
    /// </example>
    public static string FieldName<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        Expression<Func<T, object?>> selector)
    {
        var member = selector.Body switch
        {
            MemberExpression m => m.Member,
            // value-type properties are boxed to object via UnaryExpression (Convert)
            UnaryExpression { Operand: MemberExpression m } => m.Member,
            _ => throw new ArgumentException(
                "Expression must be a simple member access, e.g. m => m.FieldName.",
                nameof(selector))
        };

        return _fieldCache.GetOrAdd(member, static m =>
        {
            var field = m.GetCustomAttribute<VespaFieldAttribute>();
            if (field?.Name is not null)
                return field.Name;

            // Match JSON serialization convention: snake_case the property name
            return VespaSchemaBuilder.ToSnakeCase(m.Name);
        });
    }
}
