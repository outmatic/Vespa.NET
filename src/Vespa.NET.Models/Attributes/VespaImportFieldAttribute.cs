namespace Vespa.Models.Attributes;

/// <summary>
/// Declares an imported field from a parent document via a reference field.
/// Generates <c>import field referenceField.foreignField as alias {}</c> at the schema level.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class VespaImportFieldAttribute(string referenceField, string foreignField, string alias) : Attribute
{
    /// <summary>The reference field name in this document (e.g. "campaign_ref").</summary>
    public string ReferenceField { get; } = referenceField;

    /// <summary>The field name in the referenced document (e.g. "budget").</summary>
    public string ForeignField { get; } = foreignField;

    /// <summary>The local alias for the imported field (e.g. "campaign_budget").</summary>
    public string Alias { get; } = alias;
}
