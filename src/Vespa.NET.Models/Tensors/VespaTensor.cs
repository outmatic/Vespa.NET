using System.Numerics;
using System.Text.Json.Serialization;

namespace Vespa.Models.Tensors;

/// <summary>
/// Universal representation of a Vespa tensor that supports all formats and value types
/// Supports: float, double, int8 (sbyte), bfloat16 (stored as float — lossless)
/// </summary>
public sealed class VespaTensor
{
    /// <summary>
    /// Tensor type specification (e.g., "tensor&lt;float&gt;(x[384])")
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Format used for serialization/deserialization
    /// </summary>
    [JsonIgnore]
    public TensorFormat Format { get; set; }

    /// <summary>
    /// Internal storage for dense values (can be float[], double[], sbyte[], or Half[])
    /// </summary>
    [JsonIgnore]
    internal Array? InternalDenseValues { get; set; }

    /// <summary>
    /// Internal storage for mapped values (can be Dictionary&lt;string, T&gt; where T is numeric)
    /// </summary>
    [JsonIgnore]
    internal object? InternalMappedValues { get; set; }

    /// <summary>
    /// Internal storage for mixed single sparse (can be Dictionary&lt;string, T[]&gt;)
    /// </summary>
    [JsonIgnore]
    internal object? InternalMixedSingleSparse { get; set; }

    /// <summary>
    /// Blocks for mixed tensor with multiple sparse dimensions
    /// Format: {"blocks": [{"address": {...}, "values": [...]}]}
    /// </summary>
    [JsonIgnore]
    public List<TensorBlock>? Blocks { get; set; }

    /// <summary>
    /// Cells for verbose format (universal)
    /// Format: [{"address": {"x": "a"}, "value": 1.0}]
    /// </summary>
    [JsonIgnore]
    public List<TensorCell>? Cells { get; set; }

    /// <summary>
    /// Get dense values as typed array (zero-copy)
    /// </summary>
    public T[]? GetDenseValues<T>() where T : struct, INumber<T>
    {
        return InternalDenseValues as T[];
    }

    /// <summary>
    /// Set dense values from typed array (zero-copy)
    /// </summary>
    public void SetDenseValues<T>(T[] values) where T : struct, INumber<T>
    {
        InternalDenseValues = values;
        Format = TensorFormat.IndexedDense;
    }

    /// <summary>
    /// Get mapped values as typed dictionary (zero-copy)
    /// </summary>
    public Dictionary<string, T>? GetMappedValues<T>() where T : struct, INumber<T>
    {
        return InternalMappedValues as Dictionary<string, T>;
    }

    /// <summary>
    /// Set mapped values from typed dictionary (zero-copy)
    /// </summary>
    public void SetMappedValues<T>(Dictionary<string, T> values) where T : struct, INumber<T>
    {
        InternalMappedValues = values;
        Format = TensorFormat.MappedSingle;
    }

    /// <summary>
    /// Get mixed single sparse values as typed dictionary (zero-copy)
    /// </summary>
    public Dictionary<string, T[]>? GetMixedSingleSparse<T>() where T : struct, INumber<T>
    {
        return InternalMixedSingleSparse as Dictionary<string, T[]>;
    }

    /// <summary>
    /// Set mixed single sparse values from typed dictionary (zero-copy)
    /// </summary>
    public void SetMixedSingleSparse<T>(Dictionary<string, T[]> values) where T : struct, INumber<T>
    {
        InternalMixedSingleSparse = values;
        Format = TensorFormat.MixedSingleSparse;
    }

    /// <summary>
    /// Detect value type from Vespa type string (e.g., "tensor&lt;float&gt;(x[8])")
    /// Returns typeof(float), typeof(double), typeof(sbyte), or typeof(Half)
    /// </summary>
    public static Type DetectValueType(string? typeSpec)
    {
        if (string.IsNullOrEmpty(typeSpec))
            return typeof(double); // Default

        if (typeSpec.Contains("<float>", StringComparison.Ordinal))
            return typeof(float);
        if (typeSpec.Contains("<double>", StringComparison.Ordinal))
            return typeof(double);
        if (typeSpec.Contains("<int8>", StringComparison.Ordinal))
            return typeof(sbyte);
        // bfloat16 has float32 range (±3.4e38); System.Half (binary16) saturates at
        // 65504, so float is the lossless in-memory representation.
        if (typeSpec.Contains("<bfloat16>", StringComparison.Ordinal))
            return typeof(float);

        return typeof(double); // Default fallback
    }

    /// <summary>
    /// Create a dense indexed tensor with generic value type (zero-copy)
    /// </summary>
    public static VespaTensor FromDenseValues<T>(T[] values, string? type = null) where T : struct, INumber<T>
    {
        var tensor = new VespaTensor
        {
            Type = type,
            Format = TensorFormat.IndexedDense
        };
        tensor.SetDenseValues(values);
        return tensor;
    }

    /// <summary>
    /// Create a mapped tensor with generic value type (zero-copy)
    /// </summary>
    public static VespaTensor FromMappedValues<T>(Dictionary<string, T> values, string? type = null) where T : struct, INumber<T>
    {
        var tensor = new VespaTensor
        {
            Type = type,
            Format = TensorFormat.MappedSingle
        };
        tensor.SetMappedValues(values);
        return tensor;
    }

    /// <summary>
    /// Create a mixed tensor with single sparse dimension and generic value type (zero-copy)
    /// </summary>
    public static VespaTensor FromMixedSingleSparse<T>(Dictionary<string, T[]> values, string? type = null) where T : struct, INumber<T>
    {
        var tensor = new VespaTensor
        {
            Type = type,
            Format = TensorFormat.MixedSingleSparse
        };
        tensor.SetMixedSingleSparse(values);
        return tensor;
    }

    /// <summary>
    /// Create a mixed tensor with multiple sparse dimensions
    /// </summary>
    public static VespaTensor FromBlocks(List<TensorBlock> blocks, string? type = null)
    {
        return new VespaTensor
        {
            Type = type,
            Format = TensorFormat.MixedMultiSparse,
            Blocks = blocks
        };
    }

    /// <summary>
    /// Create a tensor in verbose format
    /// </summary>
    public static VespaTensor FromCells(List<TensorCell> cells, string? type = null)
    {
        return new VespaTensor
        {
            Type = type,
            Format = TensorFormat.Verbose,
            Cells = cells
        };
    }
}
