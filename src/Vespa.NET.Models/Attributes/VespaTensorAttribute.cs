using Vespa.Models.Schema;
using Vespa.Models.Tensors;

namespace Vespa.Models.Attributes;

/// <summary>
/// Attribute to specify Vespa tensor metadata for schema generation
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public class VespaTensorAttribute : Attribute
{
    /// <summary>
    /// Vespa tensor type specification
    /// </summary>
    public TensorTypeSpec TensorTypeSpec { get; }

    /// <summary>
    /// Preferred format for JSON serialization
    /// </summary>
    public TensorFormat PreferredFormat { get; set; } = TensorFormat.IndexedDense;

    /// <summary>
    /// Whether this tensor should be indexed for nearest neighbor search
    /// </summary>
    public bool EnableIndex { get; set; } = false;

    /// <summary>
    /// Whether this tensor should be included in document summaries (returned with search results).
    /// Default: <see langword="false"/> — embeddings are typically large and not needed in results.
    /// </summary>
    public bool IncludeInSummary { get; set; } = false;

    /// <summary>
    /// Index type for nearest neighbor search
    /// </summary>
    public TensorIndexType IndexType { get; set; } = TensorIndexType.Hnsw;

    /// <summary>
    /// Distance metric for nearest neighbor search
    /// </summary>
    public DistanceMetric DistanceMetric { get; set; } = DistanceMetric.Angular;

    /// <summary>
    /// Maximum number of links per node in the HNSW graph.
    /// Only used when <see cref="EnableIndex"/> is <see langword="true"/>.
    /// Default: 16.
    /// </summary>
    public int MaxLinksPerNode { get; set; } = 16;

    /// <summary>
    /// Number of neighbors to explore at insert time in the HNSW graph.
    /// Only used when <see cref="EnableIndex"/> is <see langword="true"/>.
    /// Default: 200.
    /// </summary>
    public int NeighborsToExploreAtInsert { get; set; } = 200;

    /// <summary>
    /// Create a VespaTensor attribute with a type-safe tensor type specification
    /// </summary>
    /// <param name="tensorTypeSpec">Tensor type specification builder</param>
    public VespaTensorAttribute(TensorTypeSpec tensorTypeSpec)
    {
        ArgumentNullException.ThrowIfNull(tensorTypeSpec);
        TensorTypeSpec = tensorTypeSpec;
    }

    /// <summary>
    /// Create a VespaTensor attribute from a string tensor type specification
    /// </summary>
    /// <param name="tensorType">Vespa tensor type string (e.g., "tensor&lt;float&gt;(x[384])")</param>
    public VespaTensorAttribute(string tensorType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tensorType);
        TensorTypeSpec = TensorTypeSpec.Parse(tensorType);
    }

    /// <summary>
    /// Get the tensor type as a string (for schema generation)
    /// </summary>
    public string GetTensorTypeString() => TensorTypeSpec.ToString();
}
