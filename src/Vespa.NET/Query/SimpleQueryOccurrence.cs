namespace Vespa.Query;

/// <summary>Controls the sign on a word or phrase in Vespa's Simple Query Language.</summary>
public enum SimpleQueryOccurrence
{
    /// <summary>Leaves the term unprefixed. In a model filter it contributes to ranking.</summary>
    Default,

    /// <summary>Adds a plus sign. Requires the term in a model filter or a query of type <c>any</c>.</summary>
    Required,

    /// <summary>Adds a minus sign. Excludes documents matching the term.</summary>
    Excluded
}
