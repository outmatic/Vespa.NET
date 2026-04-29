namespace Vespa.Models;

/// <summary>
/// Represents a basic Vespa operation response
/// </summary>
public sealed record VespaResponse
{
    public bool IsSuccess { get; init; }
    public int StatusCode { get; init; }
    public string? Message { get; init; }

    /// <summary>
    /// Fields that were present in the document but ignored by Vespa
    /// (returned via <c>X-Vespa-Ignored-Fields</c> response header).
    /// </summary>
    public IReadOnlyList<string>? IgnoredFields { get; init; }

    /// <summary>
    /// Number of documents affected by a selection operation
    /// (<c>UpdateBySelectionAsync</c>, <c>DeleteBySelectionAsync</c>, <c>CopyBySelectionAsync</c>),
    /// summed across all continuation chunks. <see langword="null"/> for single-document operations.
    /// </summary>
    public long? DocumentCount { get; init; }
}
