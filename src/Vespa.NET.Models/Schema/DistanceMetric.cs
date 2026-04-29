namespace Vespa.Models.Schema;

/// <summary>
/// Distance metrics for tensor similarity/nearest neighbor search
/// </summary>
public enum DistanceMetric
{
    /// <summary>
    /// No distance metric specified
    /// </summary>
    None,

    /// <summary>
    /// Euclidean distance: sqrt(sum((a[i] - b[i])^2))
    /// Best for: General purpose, when magnitude matters
    /// </summary>
    Euclidean,

    /// <summary>
    /// Angular distance (cosine similarity): 1 - (dot(a,b) / (||a|| * ||b||))
    /// Best for: Text embeddings, when only direction matters
    /// </summary>
    Angular,

    /// <summary>
    /// Dot product: sum(a[i] * b[i])
    /// Best for: Pre-normalized vectors, faster than angular
    /// Note: Higher is better (not a true distance metric)
    /// </summary>
    DotProduct,

    /// <summary>
    /// Hamming distance: count of positions where bits differ
    /// Best for: Binary vectors, hash codes
    /// </summary>
    Hamming,

    /// <summary>
    /// Geodesic distance: angular distance on unit sphere
    /// Best for: Normalized vectors on sphere surface
    /// </summary>
    Geodesic,

    /// <summary>
    /// Inner product: sum(a[i] * b[i])
    /// Similar to dot product but used in different contexts
    /// </summary>
    InnerProduct,

    /// <summary>
    /// Prenormalized angular distance (assumes vectors are already normalized)
    /// Faster than angular as it skips normalization
    /// </summary>
    PrenormalizedAngular
}
