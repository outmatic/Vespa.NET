namespace Vespa.Models.Schema;

/// <summary>
/// Stemming modes for Vespa fields
/// </summary>
public enum StemmingMode
{
    /// <summary>No stemming mode specified (use Vespa default)</summary>
    None,

    /// <summary>Best available stemming for the language</summary>
    Best,

    /// <summary>Generate multiple stems per word</summary>
    Multiple,

    /// <summary>Disable stemming</summary>
    Off
}
