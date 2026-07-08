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
    /// <see langword="true"/> when the put or update contained one or more fields that
    /// Vespa ignored (e.g. fields not present in the document type). Vespa signals this
    /// via the <c>X-Vespa-Ignored-Fields: true</c> response header — it does not report
    /// which fields were ignored.
    /// </summary>
    public bool HadIgnoredFields { get; init; }

    /// <summary>
    /// Number of documents affected by a selection operation
    /// (<c>UpdateBySelectionAsync</c>, <c>DeleteBySelectionAsync</c>, <c>CopyBySelectionAsync</c>),
    /// summed across all continuation chunks. <see langword="null"/> for single-document operations.
    /// </summary>
    public long? DocumentCount { get; init; }
}
