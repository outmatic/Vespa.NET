namespace Vespa.Models.Schema;

/// <summary>
/// Indexing modes for Vespa fields
/// </summary>
[Flags]
public enum IndexingMode
{
    /// <summary>
    /// No indexing specified
    /// </summary>
    None = 0,

    /// <summary>
    /// Index field for text search
    /// </summary>
    Index = 1 << 0,

    /// <summary>
    /// Store as attribute (fast access, sorting, grouping)
    /// </summary>
    Attribute = 1 << 1,

    /// <summary>
    /// Include in document summary (returned with results)
    /// </summary>
    Summary = 1 << 2,

    /// <summary>
    /// Combination: index + attribute + summary (most common)
    /// </summary>
    IndexAttributeSummary = Index | Attribute | Summary,

    /// <summary>
    /// Combination: attribute + summary (for non-searchable fields)
    /// </summary>
    AttributeSummary = Attribute | Summary,

    /// <summary>
    /// Combination: summary + index (text-searchable without attribute storage)
    /// </summary>
    SummaryIndex = Summary | Index
}
