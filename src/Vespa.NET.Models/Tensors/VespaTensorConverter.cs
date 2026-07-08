using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vespa.Models.Tensors;

/// <summary>
/// Universal JSON converter for Vespa tensors that supports all documented forms:
/// dense arrays (including nested multi-dimensional arrays, flattened row-major),
/// mapped/mixed short forms, verbose cells, blocks, and hex-encoded dense values
/// (<c>presentation.format.tensors=hex|hex-value</c>).
/// Efficiently handles float, double, int8 (sbyte), and bfloat16 (stored as float) cell types.
/// </summary>
public sealed class VespaTensorConverter : JsonConverter<VespaTensor?>
{
    public override VespaTensor? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            // presentation.format.tensors=hex-value renders dense tensors as a bare hex
            // string. Without a type spec the cell width is unknown, so the value is
            // decoded as double (16 hex digits per cell).
            JsonTokenType.String => FromDenseArray(ParseHexValues(reader.GetString()!, null, typeof(double)), null),
            JsonTokenType.StartArray => ReadArrayFormat(ref reader, null),
            JsonTokenType.StartObject => ReadObjectFormat(ref reader),
            _ => throw new JsonException($"Unexpected token type for tensor: {reader.TokenType}")
        };
    }

    private static VespaTensor ReadArrayFormat(ref Utf8JsonReader reader, string? tensorType)
    {
        // Could be:
        // 1. Dense indexed: [1.0, 2.0, 3.0] or nested [[2.0, 3.0], [5.0, 7.0]]
        // 2. Verbose cells: [{"address": {...}, "value": ...}]
        // 3. Blocks: [{"address": {...}, "values": [...]}]

        var valueType = VespaTensor.DetectValueType(tensorType);

        if (!reader.Read())
            throw new JsonException("Unexpected end of JSON while reading a tensor array");

        if (reader.TokenType == JsonTokenType.EndArray)
            return CreateEmptyDenseTensor(valueType, tensorType);

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var cells = new List<TensorCell>();
            var blocks = new List<TensorBlock>();

            ParseCellOrBlock(ref reader, valueType, tensorType, cells, blocks);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                if (reader.TokenType == JsonTokenType.StartObject)
                    ParseCellOrBlock(ref reader, valueType, tensorType, cells, blocks);

            return BuildCellsOrBlocksTensor(cells, blocks, tensorType);
        }

        // Dense format — read efficiently based on the detected cell type
        return ReadDenseArray(ref reader, valueType, tensorType);
    }

    private delegate T ReadJsonNumber<T>(ref Utf8JsonReader reader);

    private static VespaTensor ReadDenseArray(ref Utf8JsonReader reader, Type valueType, string? tensorType)
    {
        if (valueType == typeof(float))
            return FromDenseArray(ReadFlatDenseValues(ref reader, static (ref Utf8JsonReader r) => r.GetSingle()), tensorType);

        if (valueType == typeof(sbyte))
            return FromDenseArray(ReadFlatDenseValues(ref reader, static (ref Utf8JsonReader r) => ToInt8(r.GetDouble())), tensorType);

        return FromDenseArray(ReadFlatDenseValues(ref reader, static (ref Utf8JsonReader r) => r.GetDouble()), tensorType);
    }

    /// <summary>
    /// Reads dense values with the reader positioned on the first element of the array.
    /// Multi-dimensional indexed tensors use nested arrays (<c>[[2.0,3.0],[5.0,7.0]]</c>);
    /// they are flattened row-major, matching Vespa's standard value order.
    /// </summary>
    private static T[] ReadFlatDenseValues<T>(ref Utf8JsonReader reader, ReadJsonNumber<T> readValue)
    {
        var values = new List<T>();
        var depth = 0;

        while (true)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    values.Add(readValue(ref reader));
                    break;

                case JsonTokenType.StartArray:
                    depth++;
                    break;

                case JsonTokenType.EndArray:
                    if (depth == 0)
                        return [.. values];
                    depth--;
                    break;

                default:
                    throw new JsonException($"Unexpected token {reader.TokenType} in dense tensor array");
            }

            if (!reader.Read())
                throw new JsonException("Unexpected end of JSON while reading a dense tensor array");
        }
    }

    private static VespaTensor FromDenseArray(Array values, string? tensorType) =>
        new() { Type = tensorType, Format = TensorFormat.IndexedDense, InternalDenseValues = values };

    private static VespaTensor CreateEmptyDenseTensor(Type valueType, string? tensorType)
    {
        if (valueType == typeof(float))
            return VespaTensor.FromDenseValues(Array.Empty<float>(), tensorType);
        if (valueType == typeof(sbyte))
            return VespaTensor.FromDenseValues(Array.Empty<sbyte>(), tensorType);
        return VespaTensor.FromDenseValues(Array.Empty<double>(), tensorType);
    }

    private static VespaTensor CreateEmptyMappedTensor(Type valueType, string? tensorType)
    {
        if (valueType == typeof(float))
            return VespaTensor.FromMappedValues(new Dictionary<string, float>(), tensorType);
        if (valueType == typeof(sbyte))
            return VespaTensor.FromMappedValues(new Dictionary<string, sbyte>(), tensorType);
        return VespaTensor.FromMappedValues(new Dictionary<string, double>(), tensorType);
    }

    private static VespaTensor ReadObjectFormat(ref Utf8JsonReader reader)
    {
        // First pass: collect all properties to find "type" field
        string? tensorType = null;

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Extract type first if present
        if (root.TryGetProperty("type", out var typeElem))
            tensorType = typeElem.GetString();

        var valueType = VespaTensor.DetectValueType(tensorType);

        if (root.TryGetProperty("blocks", out var blocksElem))
        {
            // Short form for a single mapped dimension: {"blocks":{"a":[1.0,2.0]}}
            return blocksElem.ValueKind == JsonValueKind.Object
                ? ParseMixedSingleSparse(blocksElem.EnumerateObject().ToList(), valueType, tensorType)
                : ParseBlocks(blocksElem, valueType, tensorType);
        }

        if (root.TryGetProperty("cells", out var cellsElem))
            return ParseCellsProperty(cellsElem, valueType, tensorType);

        if (root.TryGetProperty("values", out var valuesElem))
            return FromDenseArray(ParseTypedValuesElement(valuesElem, tensorType, valueType), tensorType);

        // Could be mapped or mixed sparse
        return ParseMappedOrMixed(root, valueType, tensorType);
    }

    private static VespaTensor ParseCellsProperty(JsonElement cellsElem, Type valueType, string? tensorType)
    {
        // Short form mapped: {"cells":{"a":2.0}}
        if (cellsElem.ValueKind == JsonValueKind.Object)
            return ParseMapped(cellsElem.EnumerateObject().ToList(), valueType, tensorType);

        // Hex-encoded dense values (presentation.format.tensors=hex)
        if (cellsElem.ValueKind == JsonValueKind.String)
            return FromDenseArray(ParseHexValues(cellsElem.GetString()!, tensorType, valueType), tensorType);

        if (cellsElem.ValueKind != JsonValueKind.Array)
            throw new JsonException($"Unexpected JSON value for tensor 'cells': {cellsElem.ValueKind}");

        var first = cellsElem.EnumerateArray().FirstOrDefault();

        // Empty cells belong to a mapped/sparse tensor unless the type says otherwise
        // (a dense tensor always has all its cells): an empty dense tensor would
        // round-trip to [], which is invalid for a mapped field.
        if (first.ValueKind == JsonValueKind.Undefined)
            return tensorType is not null && tensorType.Contains('[') && !tensorType.Contains('{')
                ? CreateEmptyDenseTensor(valueType, tensorType)
                : CreateEmptyMappedTensor(valueType, tensorType);

        // Long form (default document/v1 rendering): {"cells":[{"address":{...},"value":...}]}
        if (first.ValueKind == JsonValueKind.Object)
            return ParseCellsAndBlocks(cellsElem, valueType, tensorType);

        return FromDenseArray(ParseTypedValuesElement(cellsElem, tensorType, valueType), tensorType);
    }

    /// <summary>
    /// Parses dense values from an array (nested arrays flattened row-major, matching
    /// Vespa's standard dimension order) or from a hex-encoded string.
    /// </summary>
    private static Array ParseTypedValuesElement(JsonElement element, string? tensorType, Type valueType)
    {
        if (element.ValueKind == JsonValueKind.String)
            return ParseHexValues(element.GetString()!, tensorType, valueType);

        if (element.ValueKind != JsonValueKind.Array)
            throw new JsonException($"Expected an array or hex string for tensor values, got {element.ValueKind}");

        var flat = new List<JsonElement>();
        FlattenValues(element, flat);

        if (valueType == typeof(float))
            return flat.Select(e => e.GetSingle()).ToArray();

        if (valueType == typeof(sbyte))
            return flat.Select(e => ToInt8(e.GetDouble())).ToArray();

        return flat.Select(e => e.GetDouble()).ToArray();
    }

    private static void FlattenValues(JsonElement array, List<JsonElement> output)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array)
                FlattenValues(item, output);
            else
                output.Add(item);
        }
    }

    private static VespaTensor ParseCellsAndBlocks(JsonElement arrayElem, Type valueType, string? tensorType)
    {
        var cells = new List<TensorCell>();
        var blocks = new List<TensorBlock>();

        foreach (var element in arrayElem.EnumerateArray())
            ParseCellOrBlockElement(element, valueType, tensorType, cells, blocks);

        return BuildCellsOrBlocksTensor(cells, blocks, tensorType);
    }

    private static void ParseCellOrBlock(
        ref Utf8JsonReader reader, Type valueType, string? tensorType, List<TensorCell> cells, List<TensorBlock> blocks)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        ParseCellOrBlockElement(doc.RootElement, valueType, tensorType, cells, blocks);
    }

    /// <summary>
    /// Parses one entry of a cell/block array: <c>{"address":…,"value":…}</c> is a verbose
    /// cell, <c>{"address":…,"values":…}</c> is a block. Objects with neither are rejected
    /// instead of silently producing a zero-valued cell.
    /// </summary>
    private static void ParseCellOrBlockElement(
        JsonElement element, Type valueType, string? tensorType, List<TensorCell> cells, List<TensorBlock> blocks)
    {
        var address = element.TryGetProperty("address", out var addrElem)
            ? addrElem.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty)
            : new Dictionary<string, string>();

        if (element.TryGetProperty("values", out var valuesElem))
            blocks.Add(new TensorBlock
            {
                Address = address,
                InternalValues = ParseTypedValuesElement(valuesElem, tensorType, valueType)
            });
        else if (element.TryGetProperty("value", out var valueElem))
            cells.Add(new TensorCell { Address = address, Value = ReadCellValue(valueElem, valueType) });
        else
            throw new JsonException("Tensor cell object must contain a 'value' (cell) or 'values' (block) property");
    }

    private static object ReadCellValue(JsonElement valueElem, Type valueType)
    {
        if (valueType == typeof(float))
            return valueElem.GetSingle();
        if (valueType == typeof(sbyte))
            return ToInt8(valueElem.GetDouble());
        return valueElem.GetDouble();
    }

    private static VespaTensor BuildCellsOrBlocksTensor(List<TensorCell> cells, List<TensorBlock> blocks, string? tensorType)
    {
        if (cells.Count > 0 && blocks.Count > 0)
            throw new JsonException("Tensor array mixes cell objects ('value') with block objects ('values')");

        return blocks.Count > 0
            ? VespaTensor.FromBlocks(blocks, tensorType)
            : VespaTensor.FromCells(cells, tensorType);
    }

    private static VespaTensor ParseMappedOrMixed(JsonElement root, Type valueType, string? tensorType)
    {
        // Skip "type" property
        var properties = root.EnumerateObject().Where(p => p.Name != "type").ToList();

        if (properties.Count == 0)
            return CreateEmptyDenseTensor(valueType, tensorType);

        return properties[0].Value.ValueKind switch
        {
            // Mapped format: {"a": 1.0, "b": 2.0}
            JsonValueKind.Number => ParseMapped(properties, valueType, tensorType),
            // Mixed single sparse: {"x1": [1.0, 2.0]} — values may also be hex strings
            JsonValueKind.Array or JsonValueKind.String => ParseMixedSingleSparse(properties, valueType, tensorType),
            _ => throw new JsonException("Unable to determine tensor format")
        };
    }

    private static VespaTensor ParseMapped(List<JsonProperty> properties, Type valueType, string? tensorType)
    {
        if (valueType == typeof(float))
            return VespaTensor.FromMappedValues(properties.ToDictionary(p => p.Name, p => p.Value.GetSingle()), tensorType);

        if (valueType == typeof(sbyte))
            return VespaTensor.FromMappedValues(properties.ToDictionary(p => p.Name, p => ToInt8(p.Value.GetDouble())), tensorType);

        return VespaTensor.FromMappedValues(properties.ToDictionary(p => p.Name, p => p.Value.GetDouble()), tensorType);
    }

    private static VespaTensor ParseMixedSingleSparse(List<JsonProperty> properties, Type valueType, string? tensorType)
    {
        if (valueType == typeof(float))
            return VespaTensor.FromMixedSingleSparse(
                properties.ToDictionary(p => p.Name, p => (float[])ParseTypedValuesElement(p.Value, tensorType, valueType)),
                tensorType);

        if (valueType == typeof(sbyte))
            return VespaTensor.FromMixedSingleSparse(
                properties.ToDictionary(p => p.Name, p => (sbyte[])ParseTypedValuesElement(p.Value, tensorType, valueType)),
                tensorType);

        return VespaTensor.FromMixedSingleSparse(
            properties.ToDictionary(p => p.Name, p => (double[])ParseTypedValuesElement(p.Value, tensorType, valueType)),
            tensorType);
    }

    private static VespaTensor ParseBlocks(JsonElement blocksElem, Type valueType, string? tensorType)
    {
        var blocks = new List<TensorBlock>();

        foreach (var blockElem in blocksElem.EnumerateArray())
        {
            if (!blockElem.TryGetProperty("values", out var valuesElem))
                throw new JsonException("Tensor block object must contain a 'values' property");

            blocks.Add(new TensorBlock
            {
                Address = blockElem.TryGetProperty("address", out var addrElem)
                    ? addrElem.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty)
                    : new Dictionary<string, string>(),
                InternalValues = ParseTypedValuesElement(valuesElem, tensorType, valueType)
            });
        }

        return VespaTensor.FromBlocks(blocks, tensorType);
    }

    /// <summary>
    /// Decodes the documented hex string form for dense tensor values: each cell is the
    /// big-endian bit pattern of its value — 2 hex digits for int8, 4 for bfloat16,
    /// 8 for float, 16 for double. Without a type spec the value is decoded as double.
    /// </summary>
    private static Array ParseHexValues(string hex, string? tensorType, Type valueType)
    {
        if (valueType == typeof(sbyte))
        {
            var values = new sbyte[HexCellCount(hex, 2)];
            for (var i = 0; i < values.Length; i++)
                values[i] = unchecked((sbyte)ParseHexCell(hex, i, 2));
            return values;
        }

        // bfloat16 is stored in memory as float but hex-encoded as 2 bytes
        // (the upper half of the float32 bit pattern).
        if (tensorType?.Contains("<bfloat16>", StringComparison.Ordinal) == true)
        {
            var values = new float[HexCellCount(hex, 4)];
            for (var i = 0; i < values.Length; i++)
                values[i] = BitConverter.UInt32BitsToSingle((uint)ParseHexCell(hex, i, 4) << 16);
            return values;
        }

        if (valueType == typeof(float))
        {
            var values = new float[HexCellCount(hex, 8)];
            for (var i = 0; i < values.Length; i++)
                values[i] = BitConverter.UInt32BitsToSingle((uint)ParseHexCell(hex, i, 8));
            return values;
        }

        var doubles = new double[HexCellCount(hex, 16)];
        for (var i = 0; i < doubles.Length; i++)
            doubles[i] = BitConverter.UInt64BitsToDouble(ParseHexCell(hex, i, 16));
        return doubles;
    }

    private static int HexCellCount(string hex, int digitsPerCell) =>
        hex.Length % digitsPerCell == 0
            ? hex.Length / digitsPerCell
            : throw new JsonException(
                $"Hex tensor value length {hex.Length} is not a multiple of {digitsPerCell} hex digits per cell");

    private static ulong ParseHexCell(string hex, int index, int digitsPerCell) =>
        ulong.TryParse(hex.AsSpan(index * digitsPerCell, digitsPerCell),
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new JsonException(
                $"Invalid hex digits in tensor value: '{hex.Substring(index * digitsPerCell, digitsPerCell)}'");

    /// <summary>
    /// Converts a JSON-rendered cell value (Vespa renders int8 cells as doubles, e.g. 2.0)
    /// to <see cref="sbyte"/>, failing loudly instead of saturating/truncating.
    /// </summary>
    private static sbyte ToInt8(double value) =>
        double.IsInteger(value) && value is >= sbyte.MinValue and <= sbyte.MaxValue
            ? (sbyte)value
            : throw new JsonException($"Value {value} is not a valid int8 tensor cell (integer in [-128, 127] expected).");

    public override void Write(Utf8JsonWriter writer, VespaTensor? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        switch (value.Format)
        {
            case TensorFormat.IndexedDense:
                WriteIndexedDense(writer, value);
                break;

            case TensorFormat.MappedSingle:
                WriteMappedSingle(writer, value);
                break;

            case TensorFormat.MixedSingleSparse:
                WriteMixedSingleSparse(writer, value);
                break;

            case TensorFormat.MixedMultiSparse:
                WriteMixedMultiSparse(writer, value);
                break;

            case TensorFormat.Verbose:
                WriteVerbose(writer, value);
                break;

            default:
                throw new JsonException($"Unsupported tensor format: {value.Format}");
        }
    }

    private static void WriteIndexedDense(Utf8JsonWriter writer, VespaTensor tensor)
    {
        if (tensor.InternalDenseValues == null)
            throw new JsonException("InternalDenseValues is null for IndexedDense format");

        writer.WriteStartArray();

        foreach (var val in tensor.InternalDenseValues)
        {
            WriteNumber(writer, val);
        }

        writer.WriteEndArray();
    }

    private static void WriteMappedSingle(Utf8JsonWriter writer, VespaTensor tensor)
    {
        if (tensor.InternalMappedValues == null)
            throw new JsonException("InternalMappedValues is null for MappedSingle format");

        writer.WriteStartObject();

        // Fast paths for the storage types produced by this converter
        if (tensor.InternalMappedValues is Dictionary<string, float> floatDict)
        {
            foreach (var kvp in floatDict)
                writer.WriteNumber(kvp.Key, kvp.Value);
        }
        else if (tensor.InternalMappedValues is Dictionary<string, double> doubleDict)
        {
            foreach (var kvp in doubleDict)
                writer.WriteNumber(kvp.Key, kvp.Value);
        }
        else if (tensor.InternalMappedValues is Dictionary<string, sbyte> sbyteDict)
        {
            foreach (var kvp in sbyteDict)
                writer.WriteNumber(kvp.Key, kvp.Value);
        }
        else if (tensor.InternalMappedValues is Dictionary<string, Half> halfDict)
        {
            foreach (var kvp in halfDict)
                writer.WriteNumber(kvp.Key, (float)kvp.Value);
        }
        else if (tensor.InternalMappedValues is IDictionary genericDict)
        {
            // Fallback for any other numeric element type set via SetMappedValues<T>
            foreach (DictionaryEntry entry in genericDict)
            {
                if (entry.Key is not string key)
                    throw new NotSupportedException(
                        $"Mapped tensor keys must be strings, got {entry.Key?.GetType().ToString() ?? "null"}.");
                writer.WritePropertyName(key);
                WriteNumber(writer, entry.Value!);
            }
        }
        else
        {
            throw new NotSupportedException(
                $"Mapped tensor values of type {tensor.InternalMappedValues.GetType()} cannot be serialized.");
        }

        writer.WriteEndObject();
    }

    private static void WriteMixedSingleSparse(Utf8JsonWriter writer, VespaTensor tensor)
    {
        if (tensor.InternalMixedSingleSparse == null)
            throw new JsonException("InternalMixedSingleSparse is null for MixedSingleSparse format");

        writer.WriteStartObject();

        // Fast paths for the storage types produced by this converter
        if (tensor.InternalMixedSingleSparse is Dictionary<string, float[]> floatDict)
        {
            foreach (var kvp in floatDict)
            {
                writer.WritePropertyName(kvp.Key);
                writer.WriteStartArray();
                foreach (var val in kvp.Value)
                    writer.WriteNumberValue(val);
                writer.WriteEndArray();
            }
        }
        else if (tensor.InternalMixedSingleSparse is Dictionary<string, double[]> doubleDict)
        {
            foreach (var kvp in doubleDict)
            {
                writer.WritePropertyName(kvp.Key);
                writer.WriteStartArray();
                foreach (var val in kvp.Value)
                    writer.WriteNumberValue(val);
                writer.WriteEndArray();
            }
        }
        else if (tensor.InternalMixedSingleSparse is Dictionary<string, sbyte[]> sbyteDict)
        {
            foreach (var kvp in sbyteDict)
            {
                writer.WritePropertyName(kvp.Key);
                writer.WriteStartArray();
                foreach (var val in kvp.Value)
                    writer.WriteNumberValue(val);
                writer.WriteEndArray();
            }
        }
        else if (tensor.InternalMixedSingleSparse is Dictionary<string, Half[]> halfDict)
        {
            foreach (var kvp in halfDict)
            {
                writer.WritePropertyName(kvp.Key);
                writer.WriteStartArray();
                foreach (var val in kvp.Value)
                    writer.WriteNumberValue((float)val);
                writer.WriteEndArray();
            }
        }
        else if (tensor.InternalMixedSingleSparse is IDictionary genericDict)
        {
            // Fallback for any other numeric element type set via SetMixedSingleSparse<T>
            foreach (DictionaryEntry entry in genericDict)
            {
                if (entry.Key is not string key)
                    throw new NotSupportedException(
                        $"Mixed tensor keys must be strings, got {entry.Key?.GetType().ToString() ?? "null"}.");
                if (entry.Value is not Array values)
                    throw new NotSupportedException(
                        $"Mixed tensor values must be arrays, got {entry.Value?.GetType().ToString() ?? "null"}.");
                writer.WritePropertyName(key);
                writer.WriteStartArray();
                foreach (var val in values)
                    WriteNumber(writer, val!);
                writer.WriteEndArray();
            }
        }
        else
        {
            throw new NotSupportedException(
                $"Mixed tensor values of type {tensor.InternalMixedSingleSparse.GetType()} cannot be serialized.");
        }

        writer.WriteEndObject();
    }

    private static void WriteMixedMultiSparse(Utf8JsonWriter writer, VespaTensor tensor)
    {
        if (tensor.Blocks == null)
            throw new JsonException("Blocks is null for MixedMultiSparse format");

        writer.WriteStartObject();
        writer.WritePropertyName("blocks");
        writer.WriteStartArray();

        foreach (var block in tensor.Blocks)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("address");
            writer.WriteStartObject();
            foreach (var kvp in block.Address)
                writer.WriteString(kvp.Key, kvp.Value);
            writer.WriteEndObject();

            writer.WritePropertyName("values");
            writer.WriteStartArray();
            foreach (var val in block.InternalValues)
                WriteNumber(writer, val);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteVerbose(Utf8JsonWriter writer, VespaTensor tensor)
    {
        if (tensor.Cells == null)
            throw new JsonException("Cells is null for Verbose format");

        writer.WriteStartArray();

        foreach (var cell in tensor.Cells)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("address");
            writer.WriteStartObject();
            foreach (var kvp in cell.Address)
                writer.WriteString(kvp.Key, kvp.Value);
            writer.WriteEndObject();

            writer.WritePropertyName("value");
            WriteNumber(writer, cell.Value);

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteNumber(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case float f:
                writer.WriteNumberValue(f);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case sbyte sb:
                writer.WriteNumberValue(sb);
                break;
            case Half h:
                writer.WriteNumberValue((float)h);
                break;
            default:
                double converted;
                try
                {
                    converted = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentNullException)
                {
                    throw new NotSupportedException(
                        $"Tensor cell value of type {value?.GetType().ToString() ?? "null"} cannot be serialized as a number.", ex);
                }
                writer.WriteNumberValue(converted);
                break;
        }
    }
}
