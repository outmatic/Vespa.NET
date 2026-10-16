using Vespa.Models.Schema;
using Vespa.Models.Tensors;

namespace Vespa.Models.Attributes;

/// <summary>
/// Specifies tensor metadata for schema generation and type-aware reading of
/// value-only JSON formats such as <c>hex-value</c>.
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
    /// Whether this tensor should be indexed for approximate nearest neighbor search.
    /// Also enabled when a companion <c>[VespaField]</c> requests
    /// <c>IndexingMode.Index</c>. Requires <see cref="IndexType"/> =
    /// <see cref="TensorIndexType.Hnsw"/>; combining an index request with
    /// <see cref="TensorIndexType.None"/> fails schema generation.
    /// </summary>
    public bool EnableIndex { get; set; }

    /// <summary>
    /// Whether this tensor should be included in document summaries (returned with search results).
    /// Also enabled when a companion <c>[VespaField]</c> requests <c>IndexingMode.Summary</c>.
    /// Default: <see langword="false"/> — embeddings are typically large and not needed in results.
    /// </summary>
    public bool IncludeInSummary { get; set; }

    /// <summary>
    /// Index type for nearest neighbor search. <see cref="TensorIndexType.Hnsw"/>
    /// (default) builds an HNSW graph when an index is requested;
    /// <see cref="TensorIndexType.None"/> keeps the field a plain attribute
    /// (exact, brute-force nearest neighbor).
    /// </summary>
    public TensorIndexType IndexType { get; set; } = TensorIndexType.Hnsw;

    /// <summary>
    /// Distance metric for nearest neighbor search.
    /// Defaults to <see cref="DistanceMetric.Angular"/> — the common choice for text
    /// embeddings — which deliberately diverges from Vespa's schema default
    /// (<c>euclidean</c>). Set explicitly when generating schemas for other kinds of data.
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
    /// Create a VespaTensor attribute from a string tensor type specification.
    /// (Attribute arguments must be compile-time constants, so the string form
    /// is the only usable constructor.)
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
