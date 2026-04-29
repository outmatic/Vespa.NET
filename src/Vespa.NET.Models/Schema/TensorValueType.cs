namespace Vespa.Models.Schema;

/// <summary>
/// Value types for Vespa tensors
/// </summary>
public enum TensorValueType
{
    /// <summary>
    /// 32-bit floating point (default)
    /// </summary>
    Float,

    /// <summary>
    /// 64-bit floating point (double precision)
    /// </summary>
    Double,

    /// <summary>
    /// 8-bit signed integer
    /// </summary>
    Int8,

    /// <summary>
    /// 8-bit floating point (bfloat16)
    /// </summary>
    BFloat16
}
