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

    /// <summary>
    /// Tensor field (use VespaTensorAttribute for full configuration)
    /// </summary>
    Tensor,

    /// <summary>
    /// Array of booleans
    /// </summary>
    ArrayBool,

    /// <summary>
    /// Array of bytes
    /// </summary>
    ArrayByte,

    /// <summary>
    /// Array of integers
    /// </summary>
    ArrayInt,

    /// <summary>
    /// Array of longs
    /// </summary>
    ArrayLong,

    /// <summary>
    /// Array of floats
    /// </summary>
    ArrayFloat,

    /// <summary>
    /// Array of doubles
    /// </summary>
    ArrayDouble,

    /// <summary>
    /// Array of strings
    /// </summary>
    ArrayString,

    /// <summary>
    /// Weighted set of strings
    /// </summary>
    WeightedSetString,

    /// <summary>
    /// Weighted set of integers
    /// </summary>
    WeightedSetInt,

    /// <summary>
    /// Map from string to string
    /// </summary>
    MapStringString,

    /// <summary>
    /// Map from string to int
    /// </summary>
    MapStringInt,

    /// <summary>
    /// Reference to another document
    /// </summary>
    Reference
}
