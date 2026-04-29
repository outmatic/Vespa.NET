namespace Vespa.Models.Schema;

/// <summary>
/// Match modes for text search in Vespa
/// </summary>
public enum MatchMode
{
    /// <summary>
    /// No match mode specified
    /// </summary>
    None,

    /// <summary>
    /// Full text search with tokenization and linguistic processing
    /// </summary>
    Text,

    /// <summary>
    /// Match individual words (tokenized but no stemming)
    /// </summary>
    Word,

    /// <summary>
    /// Exact string match (no tokenization)
    /// </summary>
    Exact,

    /// <summary>
    /// Prefix matching
    /// </summary>
    Prefix,

    /// <summary>
    /// Substring matching
    /// </summary>
    Substring,

    /// <summary>
    /// Suffix matching
    /// </summary>
    Suffix,

    /// <summary>
    /// Case-insensitive matching
    /// </summary>
    Cased,

    /// <summary>
    /// Case-sensitive matching
    /// </summary>
    Uncased,

    /// <summary>
    /// Gram matching (for n-gram indexes)
    /// </summary>
    Gram
}
