namespace Vespa.Models.Schema;

/// <summary>
/// Rank types for Vespa fields (how field participates in ranking)
/// </summary>
public enum RankType
{
    /// <summary>
    /// No rank type specified
    /// </summary>
    None,

    /// <summary>
    /// Use field value directly as rank contribution (default for numeric fields)
    /// </summary>
    Identity,

    /// <summary>
    /// Field is only used for filtering, not ranking
    /// </summary>
    Filter,

    /// <summary>
    /// Field contains tags (for tag-based matching)
    /// </summary>
    Tags,

    /// <summary>
    /// Use field for ranking with default text scoring
    /// </summary>
    Default
}
