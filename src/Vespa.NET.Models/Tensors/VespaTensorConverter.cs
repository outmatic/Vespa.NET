using System.Text.Json;
using System.Text.Json.Serialization;
using Vespa.Models.Schema;

namespace Vespa.Models.Tensors;

/// <summary>
/// Universal JSON converter for Vespa tensors that supports all documented forms:
/// dense arrays (including nested multi-dimensional arrays, flattened row-major),
/// mapped/mixed short forms, verbose cells, blocks, and hex-encoded dense values
/// (<c>presentation.format.tensors=hex|hex-value</c>).
/// Efficiently handles float, double, int8 (sbyte), and bfloat16 (stored as float) cell types.
/// Value-only hex forms require a schema type supplied to the constructor; Vespa.NET
/// supplies it automatically for properties with <c>[VespaTensor]</c>.
/// </summary>
public sealed class VespaTensorConverter : JsonConverter<VespaTensor?>
{
    private readonly string? _tensorType;

    /// <summary>Reads self-describing tensors and numeric short forms.</summary>
    public VespaTensorConverter() { }

    /// <summary>
    /// Supplies the schema type for value-only forms, including hex values and
    /// mapped labels that coincide with JSON wrapper property names.
    /// </summary>
    public VespaTensorConverter(string tensorType)
    {
        _tensorType = TensorTypeSpec.Parse(tensorType).ToString();
    }

    public override VespaTensor? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TensorJsonReader.Read(ref reader, _tensorType);

    public override void Write(Utf8JsonWriter writer, VespaTensor? value, JsonSerializerOptions options) =>
        TensorJsonWriter.Write(writer, value);
}
