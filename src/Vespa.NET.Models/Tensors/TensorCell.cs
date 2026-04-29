using System.Numerics;
using System.Text.Json.Serialization;

namespace Vespa.Models.Tensors;

/// <summary>
/// Represents a single cell in a Vespa tensor (verbose format)
/// </summary>
public sealed class TensorCell
{
    /// <summary>
    /// Address of the cell (dimension labels)
    /// Example: {"x": "a", "y": "0"}
    /// </summary>
    [JsonPropertyName("address")]
    public Dictionary<string, string> Address { get; set; } = new();

    /// <summary>
    /// Value at this address (can be float, double, sbyte, or Half)
    /// </summary>
    [JsonPropertyName("value")]
    public object Value { get; set; } = 0.0;

    /// <summary>
    /// Get the value as a typed number
    /// </summary>
    public T GetValue<T>() where T : struct, INumber<T>
    {
        return Value switch
        {
            T typedValue => typedValue,
            float f => T.CreateChecked(f),
            double d => T.CreateChecked(d),
            sbyte sb => T.CreateChecked(sb),
            Half h => T.CreateChecked(h),
            _ => throw new InvalidCastException($"Cannot convert {Value.GetType()} to {typeof(T)}")
        };
    }

    /// <summary>
    /// Set the value from a typed number
    /// </summary>
    public void SetValue<T>(T value) where T : struct, INumber<T>
    {
        Value = value;
    }
}
