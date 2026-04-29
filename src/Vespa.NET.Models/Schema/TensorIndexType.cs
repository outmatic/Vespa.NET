namespace Vespa.Models.Schema;

/// <summary>
/// Index types for Vespa tensor fields
/// </summary>
public enum TensorIndexType
{
    /// <summary>
    /// No index (brute force search)
    /// </summary>
    None,

    /// <summary>
    /// Hierarchical Navigable Small World (HNSW) index for approximate nearest neighbor search
    /// Best for high-dimensional vectors
    /// </summary>
    Hnsw
}
