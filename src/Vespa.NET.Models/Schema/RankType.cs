namespace Vespa.Models.Schema;

/// <summary>
/// Rank types for Vespa fields (how the field participates in ranking).
/// <see cref="Filter"/> and <see cref="Default"/> map to the field-level
/// <c>rank:</c> element; <see cref="Identity"/>, <see cref="About"/>,
/// <see cref="Tags"/> and <see cref="Empty"/> map to the <c>rank-type</c> element.
/// </summary>
public enum RankType
{
    /// <summary>
    /// No rank type specified
    /// </summary>
    None,

    /// <summary>
    /// Used for fields that contain only what this document <em>is</em> (e.g. a title).
    /// Complete identity hits get a high rank. Emitted as <c>rank-type: identity</c>.
    /// </summary>
    Identity,

    /// <summary>
    /// Field is only used for filtering, not ranking. Emitted as <c>rank: filter</c>.
    /// </summary>
    Filter,

    /// <summary>
    /// Field contains tags. Uses a logarithmic table so each additional tag
    /// contributes less. Emitted as <c>rank-type: tags</c>.
    /// </summary>
    Tags,

    /// <summary>
    /// Use field for ranking with default text scoring. Emitted as <c>rank: normal</c>.
    /// </summary>
    Default,

    /// <summary>
    /// Text which is (only) about this document (e.g. a description); partial matches
    /// and early/repeated matches rank high. This is Vespa's default rank type.
    /// Emitted as <c>rank-type: about</c>.
    /// </summary>
    About,

    /// <summary>
    /// Gives no relevancy effect on matches — for fields treated purely as filters.
    /// Emitted as <c>rank-type: empty</c>.
    /// </summary>
    Empty
}
