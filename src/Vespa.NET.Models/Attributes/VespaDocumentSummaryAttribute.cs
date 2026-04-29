namespace Vespa.Models.Attributes;

/// <summary>
/// Defines a document-summary in the Vespa schema. Apply to the document class.
/// Generates <c>document-summary name { summary field type: source_field }</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class VespaDocumentSummaryAttribute(string name, params string[] fields) : Attribute
{
    /// <summary>Summary class name</summary>
    public string Name { get; } = name;

    /// <summary>Fields included in this summary class</summary>
    public string[] Fields { get; } = fields;

    /// <summary>Optional: inherit from another summary class</summary>
    public string? Inherits { get; set; }
}
