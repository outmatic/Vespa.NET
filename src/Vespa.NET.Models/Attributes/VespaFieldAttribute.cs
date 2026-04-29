using Vespa.Models.Schema;

namespace Vespa.Models.Attributes;

/// <summary>
/// Attribute to specify Vespa field metadata for schema generation
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public class VespaFieldAttribute : Attribute
{
    /// <summary>
    /// Field name in Vespa schema (if different from property name)
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Vespa field type
    /// If not specified, will be inferred from C# type
    /// </summary>
    public VespaFieldType FieldType { get; set; } = VespaFieldType.None;

    /// <summary>
    /// Indexing mode (how the field should be indexed/stored)
    /// </summary>
    public IndexingMode IndexingMode { get; set; } = IndexingMode.AttributeSummary;

    /// <summary>
    /// Match mode for text search
    /// </summary>
    public MatchMode MatchMode { get; set; } = MatchMode.None;

    /// <summary>
    /// Rank type (how field participates in ranking)
    /// </summary>
    public RankType RankType { get; set; } = RankType.None;

    /// <summary>
    /// Stemming mode (none, best, multiple). Default is unset (Vespa default).
    /// </summary>
    public StemmingMode Stemming { get; set; } = StemmingMode.None;

    /// <summary>
    /// Enable highlighting of matching query terms in this field.
    /// </summary>
    public bool Bolding { get; set; }

    /// <summary>
    /// Enable fast-search attribute optimization (B-tree index on attribute).
    /// </summary>
    public bool FastSearch { get; set; }

    /// <summary>
    /// Normalizing mode for this field.
    /// </summary>
    public string? Normalizing { get; set; }

    /// <summary>
    /// Referenced document type for <see cref="VespaFieldType.Reference"/> fields.
    /// Example: <c>"campaign"</c> generates <c>reference&lt;campaign&gt;</c>.
    /// </summary>
    public string? ReferenceDocumentType { get; set; }
}
