using Vespa.Models.Attributes;
using Vespa.Models.Schema;

namespace Vespa.IntegrationTests.Fixtures;

// ── Schema-deploy matrix document types ───────────────────────────────────────
// Deployed alongside TestProduct by VespaFixture so a single application-package
// deploy validates schema/document inheritance, complex fields with struct-field
// attribute blocks, predicate fields, [VespaRankFunction] functions, GC selection,
// and streaming mode against a real config server.

/// <summary>Base document type — inherited by <see cref="SpecialItem"/>.</summary>
[VespaDocument("base_item", Namespace = "test")]
public sealed record BaseItem
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.SummaryIndex)]
    public string Title { get; init; } = "";

    [VespaField(Name = "base_score", IndexingMode = IndexingMode.AttributeSummary)]
    public long BaseScore { get; init; }
}

/// <summary>
/// Child document type: <c>schema special_item inherits base_item</c> plus a
/// rank profile whose functions come from <c>[VespaRankFunction]</c>.
/// Inherited fields are plain properties (no <c>[VespaField]</c>) so they are fed
/// as JSON but not redeclared in the child schema.
/// </summary>
[VespaDocument("special_item", Namespace = "test", Inherits = "base_item")]
[VespaRankProfile("fn_profile", FirstPhase = "boosted_score(2)")]
[VespaRankFunction("boosted_score(factor)", "attribute(base_score) * factor", Profile = "fn_profile")]
[VespaRankFunction("half_score", "attribute(base_score) / 2", Profile = "fn_profile")]
public sealed record SpecialItem
{
    public string Title { get; init; } = "";
    public long BaseScore { get; init; }

    [VespaField(Name = "special_note", IndexingMode = IndexingMode.SummaryIndex)]
    public string SpecialNote { get; init; } = "";
}

/// <summary>Struct type used in <see cref="ComplexItem.Dimensions"/>.</summary>
[VespaStruct("dimension_info")]
public sealed record DimensionInfo
{
    [VespaField(Name = "width")]
    public double Width { get; init; }

    [VespaField(Name = "label")]
    public string Label { get; init; } = "";
}

/// <summary>
/// Complex-field document type: map&lt;string,primitive&gt; and array-of-struct
/// fields whose <c>IndexingMode.Attribute</c> generates struct-field attribute blocks.
/// </summary>
[VespaDocument("complex_item", Namespace = "test")]
public sealed record ComplexItem
{
    [VespaField(Name = "name", IndexingMode = IndexingMode.SummaryIndex)]
    public string Name { get; init; } = "";

    /// <summary>map&lt;string,long&gt; with key/value struct-field attribute blocks.</summary>
    [VespaField(Name = "ratings", IndexingMode = IndexingMode.AttributeSummary)]
    public Dictionary<string, long>? Ratings { get; init; }

    /// <summary>map&lt;string,string&gt; kept summary-only (no attribute blocks).</summary>
    [VespaField(Name = "labels", FieldType = VespaFieldType.MapStringString, IndexingMode = IndexingMode.Summary)]
    public Dictionary<string, string>? Labels { get; init; }

    /// <summary>array&lt;struct&gt; with per-member struct-field attribute blocks.</summary>
    [VespaField(Name = "dimensions", IndexingMode = IndexingMode.AttributeSummary)]
    public List<DimensionInfo>? Dimensions { get; init; }
}

/// <summary>Predicate-field document type (arity + bounds → <c>index { arity/bounds }</c>).</summary>
[VespaDocument("target_item", Namespace = "test")]
public sealed record TargetItem
{
    [VespaField(Name = "target", FieldType = VespaFieldType.Predicate,
        PredicateArity = 2, PredicateLowerBound = 0, PredicateUpperBound = 500,
        IndexingMode = IndexingMode.AttributeSummary)]
    public string? Target { get; init; }

    [VespaField(Name = "note", IndexingMode = IndexingMode.AttributeSummary)]
    public string Note { get; init; } = "";
}

/// <summary>
/// Document type with a garbage-collection <c>Selection</c> — flows into
/// <c>services.xml</c> as <c>selection="…"</c> with <c>garbage-collection="true"</c>.
/// Feed documents with <c>KeepUntil</c> &gt; 0 so they match the selection and persist.
/// </summary>
[VespaDocument("expiring_item", Namespace = "test", Selection = "expiring_item.keep_until > 0")]
public sealed record ExpiringItem
{
    [VespaField(Name = "keep_until", IndexingMode = IndexingMode.AttributeSummary)]
    public long KeepUntil { get; init; }

    [VespaField(Name = "payload", IndexingMode = IndexingMode.AttributeSummary)]
    public string Payload { get; init; } = "";
}

/// <summary>
/// Streaming-mode document type — enables group/number-level visiting and the
/// match-mode-free term annotations (substring/suffix) of streaming search.
/// </summary>
[VespaDocument("event", Namespace = "test", Mode = DocumentMode.Streaming)]
public sealed record TestEvent
{
    [VespaField(Name = "event_name", IndexingMode = IndexingMode.SummaryIndex)]
    public string EventName { get; init; } = "";

    [VespaField(Name = "payload", IndexingMode = IndexingMode.AttributeSummary)]
    public string Payload { get; init; } = "";
}
