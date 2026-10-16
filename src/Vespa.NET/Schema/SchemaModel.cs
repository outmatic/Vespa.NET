using Vespa.Models.Attributes;

namespace Vespa.Models.Schema;

// Built in a single reflection pass by SchemaModelBuilder, rendered by the small
// emitters in VespaSchemaBuilder. All validation happens while building the model.

/// <summary>Structural shape of a field, driving indexing/struct-field emission.</summary>
internal enum FieldShape
{
    /// <summary>Primitive, array-of-primitive, weightedset, raw, uri, reference, …</summary>
    Simple,

    /// <summary>A single [VespaStruct] value.</summary>
    Struct,

    /// <summary>An array of [VespaStruct] values.</summary>
    StructCollection,

    /// <summary>A map (any key/value combination that is not a weightedset).</summary>
    Map
}

internal sealed record SchemaStructModel(string Name, IReadOnlyList<(string Name, string VespaType)> Fields);

internal sealed record RankFunctionModel(string Signature, string Expression);

internal sealed class RankProfileModel
{
    public required string Name { get; init; }
    public string? Inherits { get; init; }
    public string? FirstPhase { get; init; }
    public string? SecondPhase { get; init; }
    public int SecondPhaseRerankCount { get; init; }
    public string? GlobalPhase { get; init; }
    public int GlobalPhaseRerankCount { get; init; }
    public string? MatchFeatures { get; init; }
    public string? SummaryFeatures { get; init; }
    public string? DiversityAttribute { get; init; }
    public int DiversityMinGroups { get; init; }
    public List<RankFunctionModel> Functions { get; } = [];
    public bool EmitInputs { get; set; }
}

internal sealed class SchemaFieldModel
{
    public required string Name { get; init; }
    public required string VespaType { get; init; }
    public string? Indexing { get; init; }
    public string? Match { get; init; }
    public string? Stemming { get; init; }
    public bool Bolding { get; init; }
    public bool FastSearch { get; init; }
    public string? Normalizing { get; init; }
    public string? Rank { get; init; }
    public string? RankTypeName { get; init; }
    public IReadOnlyList<(string Path, string Indexing)> StructFields { get; init; } = [];

    // Tensor-only
    public bool IsTensor { get; init; }
    public string? DistanceMetric { get; init; }
    public bool HnswIndex { get; init; }
    public int MaxLinksPerNode { get; init; }
    public int NeighborsToExploreAtInsert { get; init; }

    // Predicate-only
    public int? PredicateArity { get; init; }
    public long? PredicateLowerBound { get; init; }
    public long? PredicateUpperBound { get; init; }
}

internal sealed class SchemaModel
{
    public required string DocType { get; init; }
    public string? Inherits { get; init; }
    public required IReadOnlyList<SchemaStructModel> Structs { get; init; }
    public required IReadOnlyList<SchemaFieldModel> Fields { get; init; }
    public required IReadOnlyList<(string QueryName, string TensorType)> TensorInputs { get; init; }
    public required IReadOnlyList<VespaImportFieldAttribute> ImportFields { get; init; }
    public required IReadOnlyList<VespaConstantAttribute> Constants { get; init; }
    public required IReadOnlyList<VespaOnnxModelAttribute> OnnxModels { get; init; }
    public required IReadOnlyList<VespaFieldSetAttribute> FieldSets { get; init; }
    public required IReadOnlyList<VespaDocumentSummaryAttribute> DocumentSummaries { get; init; }
    public required IReadOnlyList<RankProfileModel> RankProfiles { get; init; }
}
