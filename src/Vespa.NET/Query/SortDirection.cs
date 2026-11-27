namespace Vespa.Query;

/// <summary>Direction of a sort key.</summary>
public enum SortDirection
{
    /// <summary>Lowest values first.</summary>
    Ascending,

    /// <summary>Highest values first.</summary>
    Descending
}

internal static class SortDirectionExtensions
{
    internal static bool IsAscending(this SortDirection direction) => direction switch
    {
        SortDirection.Ascending => true,
        SortDirection.Descending => false,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Expected a valid sort direction.")
    };
}
