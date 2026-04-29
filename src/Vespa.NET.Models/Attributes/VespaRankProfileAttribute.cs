namespace Vespa.Models.Attributes;

/// <summary>
/// Defines a rank profile in the Vespa schema. Apply to the document class.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class VespaRankProfileAttribute(string name) : Attribute
{
    /// <summary>Rank profile name</summary>
    public string Name { get; } = name;

    /// <summary>Optional: inherit from another rank profile</summary>
    public string? Inherits { get; set; }

    /// <summary>First-phase ranking expression</summary>
    public string? FirstPhase { get; set; }

    /// <summary>Second-phase ranking expression</summary>
    public string? SecondPhase { get; set; }

    /// <summary>Second-phase rerank count</summary>
    public int SecondPhaseRerankCount { get; set; }

    /// <summary>Global-phase ranking expression</summary>
    public string? GlobalPhase { get; set; }

    /// <summary>Global-phase rerank count</summary>
    public int GlobalPhaseRerankCount { get; set; }

    /// <summary>Space-separated list of match features</summary>
    public string? MatchFeatures { get; set; }

    /// <summary>Space-separated list of summary features</summary>
    public string? SummaryFeatures { get; set; }

    /// <summary>
    /// Diversity attribute for match-phase diversification.
    /// Example: <c>DiversityAttribute = "category", DiversityMinGroups = 5</c>
    /// </summary>
    public string? DiversityAttribute { get; set; }

    /// <summary>Minimum number of diversity groups.</summary>
    public int DiversityMinGroups { get; set; }

    /// <summary>
    /// Semicolon-separated list of named function definitions.
    /// Format: <c>"myFunc(a,b): a*b; freshness: now - attribute(timestamp)"</c>
    /// </summary>
    public string? Functions { get; set; }
}
