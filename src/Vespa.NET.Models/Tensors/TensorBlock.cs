using System.Numerics;
using System.Text.Json.Serialization;

namespace Vespa.Models.Tensors;

/// <summary>
/// Represents a block in a mixed tensor with multiple sparse dimensions
/// </summary>
public sealed class TensorBlock
{
    /// <summary>
    /// Address of the block (sparse dimension labels)
    /// Example: {"x": "x1", "y": "y2"}
    /// </summary>
    [JsonPropertyName("address")]
    public Dictionary<string, string> Address { get; set; } = new();

    /// <summary>
    /// Dense values for this block (can be float[], double[], sbyte[], or Half[]).
    /// Serialized by <c>VespaTensorConverter</c>, not by the serializer directly.
    /// </summary>
    [JsonIgnore]
    internal Array InternalValues { get; set; } = Array.Empty<double>();

    /// <summary>
    /// Get values as typed array (zero-copy)
    /// </summary>
    public T[]? GetValues<T>() where T : struct, INumber<T>
    {
        return InternalValues as T[];
    }

    /// <summary>
    /// Set values from typed array (zero-copy)
    /// </summary>
    public void SetValues<T>(T[] values) where T : struct, INumber<T>
    {
        InternalValues = values;
    }
}
