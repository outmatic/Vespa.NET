namespace Vespa.Query;

/// <summary>Controls the use of approximate nearest-neighbor search.</summary>
public enum NearestNeighborMode
{
    /// <summary>Request exact nearest-neighbor search.</summary>
    Exact,

    /// <summary>Allow approximate search when an HNSW index is available.</summary>
    Approximate
}
