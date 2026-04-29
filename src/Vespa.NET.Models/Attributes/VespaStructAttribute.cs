namespace Vespa.Models.Attributes;

/// <summary>
/// Marks a class as a Vespa struct type for schema generation.
/// Properties decorated with <see cref="VespaFieldAttribute"/> become struct fields.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public class VespaStructAttribute(string? name = null) : Attribute
{
    /// <summary>Struct name in the Vespa schema. Defaults to camelCase of the class name.</summary>
    public string? Name { get; } = name;
}
