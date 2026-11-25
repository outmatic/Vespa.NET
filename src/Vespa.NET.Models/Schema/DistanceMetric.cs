namespace Vespa.Models.Schema;

/// <summary>
/// Distance metrics for tensor similarity/nearest neighbor search
/// </summary>
public enum DistanceMetric
{
    /// <summary>
    /// No distance metric specified
    /// </summary>
    None = 0,

    /// <summary>
    /// Euclidean distance: sqrt(sum((a[i] - b[i])^2))
    /// Best for: General purpose, when magnitude matters
    /// </summary>
    Euclidean = 1,

    /// <summary>
    /// Angular distance (cosine similarity): 1 - (dot(a,b) / (||a|| * ||b||))
    /// Best for: Text embeddings, when only direction matters
    /// </summary>
    Angular = 2,

    /// <summary>
    /// Maximum inner product search. Vespa reports the dot product as closeness
    /// and its negative as distance. Vector magnitudes affect the score.
    /// </summary>
    DotProduct = 3,

    /// <summary>
    /// Hamming distance: count of positions where bits differ
    /// Best for: Binary vectors, hash codes
    /// </summary>
    Hamming = 4,

    /// <summary>
    /// Geodesic distance: angular distance on unit sphere
    /// Best for: Normalized vectors on sphere surface
    /// </summary>
    Geodesic = 5,

    // Value 6 is reserved for previously compiled attributes and numeric settings.

    /// <summary>
    /// Distance of 1 minus cosine similarity for query and document vectors
    /// normalized to the same nonzero L2 norm.
    /// </summary>
    PrenormalizedAngular = 7
}
