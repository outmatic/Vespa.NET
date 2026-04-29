namespace Vespa.Models.Attributes;

/// <summary>
/// Marks a property as the Vespa document ID. The field is excluded from JSON
/// serialization (it is sent in the URL, not the document body) and is not
/// included in the generated schema.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class VespaIdAttribute : Attribute;
