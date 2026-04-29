namespace Vespa.Models.Schema;

/// <summary>
/// Vespa document mode for the content cluster in <c>services.xml</c>.
/// </summary>
public enum DocumentMode
{
    /// <summary>Full indexing mode (default).</summary>
    Index,

    /// <summary>Streaming search mode — data stored on disk, streamed at query time.</summary>
    Streaming,

    /// <summary>Store-only mode — no indexing or search, just storage.</summary>
    StoreOnly
}
