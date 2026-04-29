namespace Vespa.Models.Attributes;

/// <summary>
/// Defines a fieldset in the Vespa schema. Apply to the document class.
/// Generates <c>fieldset name { fields: field1, field2 }</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class VespaFieldSetAttribute(string name, params string[] fields) : Attribute
{
    /// <summary>Fieldset name (e.g. "default")</summary>
    public string Name { get; } = name;

    /// <summary>Fields included in the fieldset</summary>
    public string[] Fields { get; } = fields;
}
