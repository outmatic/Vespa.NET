namespace Vespa.Models.Schema;

/// <summary>
/// Vespa field types
/// </summary>
public enum VespaFieldType
{
    /// <summary>
    /// Not specified - will be inferred from C# type
    /// </summary>
    None,

    /// <summary>
    /// Boolean value (true/false)
    /// </summary>
    Bool,

    /// <summary>
    /// 8-bit signed integer
    /// </summary>
    Byte,

    /// <summary>
    /// 32-bit signed integer
    /// </summary>
    Int,

    /// <summary>
    /// 64-bit signed integer
    /// </summary>
    Long,

    /// <summary>
    /// 32-bit floating point
    /// </summary>
    Float,

    /// <summary>
    /// 64-bit floating point
    /// </summary>
    Double,

    /// <summary>
    /// String/text value
    /// </summary>
    String,

    /// <summary>
    /// Raw bytes
    /// </summary>
    Raw,

    /// <summary>
    /// URI/URL value
    /// </summary>
    Uri,

    /// <summary>
    /// Predicate (boolean expression)
    /// </summary>
    Predicate,

    // Value 11 was Tensor and remains reserved for previously compiled attributes.

    /// <summary>
    /// Array of booleans
    /// </summary>
    ArrayBool = 12,

    /// <summary>
    /// Array of bytes
    /// </summary>
    ArrayByte = 13,

    /// <summary>
    /// Array of integers
    /// </summary>
    ArrayInt = 14,

    /// <summary>
    /// Array of longs
    /// </summary>
    ArrayLong = 15,

    /// <summary>
    /// Array of floats
    /// </summary>
    ArrayFloat = 16,

    /// <summary>
    /// Array of doubles
    /// </summary>
    ArrayDouble = 17,

    /// <summary>
    /// Array of strings
    /// </summary>
    ArrayString = 18,

    /// <summary>
    /// Weighted set of strings
    /// </summary>
    WeightedSetString = 19,

    /// <summary>
    /// Weighted set of integers
    /// </summary>
    WeightedSetInt = 20,

    /// <summary>
    /// Map from string to string
    /// </summary>
    MapStringString = 21,

    /// <summary>
    /// Map from string to int
    /// </summary>
    MapStringInt = 22,

    /// <summary>
    /// Reference to another document
    /// </summary>
    Reference = 23
}
