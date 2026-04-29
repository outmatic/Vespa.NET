namespace Vespa.Models.Attributes;

/// <summary>
/// Marks a <see cref="Dictionary{TKey,TValue}"/> property as the catch-all container
/// for Vespa document fields that are not explicitly mapped to C# properties.
/// <para>
/// During deserialization, any JSON field not matched by a declared property is stored
/// in this dictionary. During serialization, entries are emitted flat alongside the
/// declared properties — exactly like MongoDB's <c>[BsonExtraElements]</c>.
/// </para>
/// <para>
/// The property type must be <c>Dictionary&lt;string, JsonElement&gt;</c> (or nullable).
/// At most one property per type may carry this attribute.
/// </para>
/// </summary>
/// <example>
/// <code>
/// public record ProductFields
/// {
///     [VespaField(IndexingMode = IndexingMode.AttributeSummary)]
///     public double Popularity { get; init; }
///
///     [VespaExtraFields]
///     public Dictionary&lt;string, JsonElement&gt;? TenantFields { get; init; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class VespaExtraFieldsAttribute : Attribute;
