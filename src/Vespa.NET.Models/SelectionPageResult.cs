namespace Vespa.Models;

/// <summary>
/// Result of a single chunk of a selection operation (<c>UpdateBySelectionPageAsync</c>,
/// <c>DeleteBySelectionPageAsync</c>, <c>CopyBySelectionPageAsync</c>).
///
/// <para>Vespa processes selection operations in time-bounded chunks and returns a
/// <see cref="Continuation"/> token while more work remains. To drive the operation
/// manually (e.g. to persist the token for crash-safe resume), call the page variant
/// in a loop, passing the returned token back in until <see cref="IsComplete"/>.</para>
/// </summary>
public sealed record SelectionPageResult
{
    /// <summary>
    /// Number of documents affected in this chunk only (not cumulative across chunks).
    /// </summary>
    public long DocumentCount { get; init; }

    /// <summary>
    /// Token to pass to the next call to resume processing.
    /// <see langword="null"/> when the operation is complete.
    /// </summary>
    public string? Continuation { get; init; }

    /// <summary>
    /// HTTP status code returned by Vespa for this chunk (usually 200).
    /// </summary>
    public int StatusCode { get; init; }

    /// <summary>
    /// Fields ignored by Vespa for this chunk (from the <c>X-Vespa-Ignored-Fields</c> header).
    /// </summary>
    public IReadOnlyList<string>? IgnoredFields { get; init; }

    /// <summary>
    /// <see langword="true"/> when no continuation token was returned — i.e. this chunk
    /// processed the final remaining documents.
    /// </summary>
    public bool IsComplete => Continuation is null;
}
