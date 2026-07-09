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
    /// Indexing mode (how the field should be indexed/stored).
    /// </summary>
    /// <remarks>
    /// For struct, array-of-struct and map fields the flags are translated instead of
    /// emitted verbatim (Vespa does not allow <c>attribute</c>/<c>index</c> on such fields):
    /// <see cref="IndexingMode.Summary"/> puts the whole field in the document summary, and
    /// <see cref="IndexingMode.Attribute"/> generates
    /// <c>struct-field &lt;name&gt; { indexing: attribute }</c> blocks for every
    /// primitive-typed struct member (for maps: <c>key</c>/<c>value</c>) — supported by
    /// Vespa on array-of-struct and map fields only, so <see cref="IndexingMode.Attribute"/>
    /// on a <em>single</em> struct field throws at generation time
    /// (use <see cref="IndexingMode.Summary"/>).
    /// <see cref="IndexingMode.Index"/> on any of these fields throws at generation time.
    /// </remarks>
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

    /// <summary>
    /// Arity for <see cref="VespaFieldType.Predicate"/> fields — mandatory in Vespa
    /// (<c>index { arity: N }</c>). Schema generation throws when a predicate field
    /// leaves this at 0. Higher arity gives faster searches at the cost of index size.
    /// </summary>
    public int PredicateArity { get; set; }

    /// <summary>
    /// Optional <c>lower-bound</c> for <see cref="VespaFieldType.Predicate"/> fields:
    /// the smallest value used in the predicates' range expressions.
    /// Leave at <see cref="long.MinValue"/> to omit.
    /// </summary>
    public long PredicateLowerBound { get; set; } = long.MinValue;

    /// <summary>
    /// Optional <c>upper-bound</c> for <see cref="VespaFieldType.Predicate"/> fields:
    /// the largest value used in the predicates' range expressions.
    /// Leave at <see cref="long.MaxValue"/> to omit.
    /// </summary>
    public long PredicateUpperBound { get; set; } = long.MaxValue;
}
