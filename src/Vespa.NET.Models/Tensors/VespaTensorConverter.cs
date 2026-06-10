using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vespa.Models.Tensors;

/// <summary>
/// Universal JSON converter for Vespa tensors that supports all formats and value types
/// Efficiently handles float, double, int8 (sbyte), and bfloat16 (stored as float)
/// </summary>
public sealed class VespaTensorConverter : JsonConverter<VespaTensor?>
{
    public override VespaTensor? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.StartArray => ReadArrayFormat(ref reader, null),
            JsonTokenType.StartObject => ReadObjectFormat(ref reader),
            _ => throw new JsonException($"Unexpected token type for tensor: {reader.TokenType}")
        };
    }

    private static VespaTensor ReadArrayFormat(ref Utf8JsonReader reader, string? tensorType)
    {
        // Could be:
        // 1. Dense indexed: [1.0, 2.0, 3.0]
        // 2. Verbose: [{"address": {...}, "value": ...}]

        var valueType = VespaTensor.DetectValueType(tensorType);

        // Read first element to determine format
        if (!reader.Read())
            return CreateEmptyTensor(valueType, tensorType);

        if (reader.TokenType == JsonTokenType.EndArray)
            return CreateEmptyTensor(valueType, tensorType);

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            // Verbose format
            var cells = new List<TensorCell> { ReadTensorCell(ref reader, valueType) };

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                    cells.Add(ReadTensorCell(ref reader, valueType));
            }

            return VespaTensor.FromCells(cells, tensorType);
        }

        // Dense format - read efficiently based on detected type
        return ReadDenseArray(ref reader, valueType, tensorType);
    }

    private static VespaTensor ReadDenseArray(ref Utf8JsonReader reader, Type valueType, string? tensorType)
    {
        // Efficiently read array of the correct type
        if (valueType == typeof(float))
        {
            var list = new List<float> { reader.GetSingle() };
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.Number)
                    list.Add(reader.GetSingle());
            }
            return VespaTensor.FromDenseValues(list.ToArray(), tensorType);
        }

        if (valueType == typeof(sbyte))
        {
            var list = new List<sbyte> { (sbyte)reader.GetDouble() };
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.Number)
                    list.Add((sbyte)reader.GetDouble());
            }
            return VespaTensor.FromDenseValues(list.ToArray(), tensorType);
        }

        if (valueType == typeof(Half))
        {
            var list = new List<Half> { (Half)reader.GetSingle() };
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.Number)
                    list.Add((Half)reader.GetSingle());
            }
            return VespaTensor.FromDenseValues(list.ToArray(), tensorType);
        }
        else // double (default)
        {
            var list = new List<double> { reader.GetDouble() };
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.Number)
                    list.Add(reader.GetDouble());
            }
            return VespaTensor.FromDenseValues(list.ToArray(), tensorType);
        }
    }

    private static VespaTensor CreateEmptyTensor(Type valueType, string? tensorType)
    {
        if (valueType == typeof(float))
            return VespaTensor.FromDenseValues(Array.Empty<float>(), tensorType);
        if (valueType == typeof(sbyte))
            return VespaTensor.FromDenseValues(Array.Empty<sbyte>(), tensorType);
        if (valueType == typeof(Half))
            return VespaTensor.FromDenseValues(Array.Empty<Half>(), tensorType);
        return VespaTensor.FromDenseValues(Array.Empty<double>(), tensorType);
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

        // Check format and parse accordingly
        if (root.TryGetProperty("blocks", out var blocksElem))
        {
            // Short form for a single mapped dimension: {"blocks":{"a":[1.0,2.0]}}
            return blocksElem.ValueKind == JsonValueKind.Object
                ? ParseMixedSingleSparse(blocksElem.EnumerateObject().ToList(), valueType, tensorType)
                : ParseBlocks(blocksElem, valueType, tensorType);
        }

        if (root.TryGetProperty("cells", out var cellsElem))
        {
            // Short form mapped: {"cells":{"a":2.0}}
            if (cellsElem.ValueKind == JsonValueKind.Object)
                return ParseMapped(cellsElem.EnumerateObject().ToList(), valueType, tensorType);

            // Long form (default document/v1 rendering): {"cells":[{"address":{...},"value":...}]}
            if (cellsElem.ValueKind == JsonValueKind.Array &&
                cellsElem.EnumerateArray().FirstOrDefault().ValueKind == JsonValueKind.Object)
                return ParseVerboseCells(cellsElem, valueType, tensorType);

            return ParseDenseFromElement(cellsElem, valueType, tensorType);
        }

        if (root.TryGetProperty("values", out var valuesElem))
        {
            return ParseDenseFromElement(valuesElem, valueType, tensorType);
        }

        // Could be mapped or mixed sparse
        return ParseMappedOrMixed(root, valueType, tensorType);
    }

    private static VespaTensor ParseDenseFromElement(JsonElement element, Type valueType, string? tensorType)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected array for tensor values");

        // Multi-dimensional indexed tensors use nested arrays ({"values":[[2.0,3.0],[5.0,7.0]]});
        // flatten row-major, which matches Vespa's standard dimension order.
        var flat = new List<JsonElement>();
        FlattenValues(element, flat);

        if (valueType == typeof(float))
            return VespaTensor.FromDenseValues(flat.Select(e => e.GetSingle()).ToArray(), tensorType);

        if (valueType == typeof(sbyte))
            return VespaTensor.FromDenseValues(flat.Select(e => (sbyte)e.GetDouble()).ToArray(), tensorType);

        if (valueType == typeof(Half))
            return VespaTensor.FromDenseValues(flat.Select(e => (Half)e.GetSingle()).ToArray(), tensorType);

        return VespaTensor.FromDenseValues(flat.Select(e => e.GetDouble()).ToArray(), tensorType);
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

    private static VespaTensor ParseVerboseCells(JsonElement cellsElem, Type valueType, string? tensorType)
    {
        var cells = new List<TensorCell>();

        foreach (var cellElem in cellsElem.EnumerateArray())
        {
            var cell = new TensorCell();

            if (cellElem.TryGetProperty("address", out var addrElem))
                cell.Address = addrElem.EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);

            if (cellElem.TryGetProperty("value", out var valElem))
            {
                if (valueType == typeof(float))
                    cell.Value = valElem.GetSingle();
                else if (valueType == typeof(sbyte))
                    cell.Value = (sbyte)valElem.GetDouble();
                else if (valueType == typeof(Half))
                    cell.Value = (Half)valElem.GetSingle();
                else
                    cell.Value = valElem.GetDouble();
            }

            cells.Add(cell);
        }

        return VespaTensor.FromCells(cells, tensorType);
    }

    private static VespaTensor ParseMappedOrMixed(JsonElement root, Type valueType, string? tensorType)
    {
        // Skip "type" property
        var properties = root.EnumerateObject().Where(p => p.Name != "type").ToList();

        if (properties.Count == 0)
            return CreateEmptyTensor(valueType, tensorType);

        var firstProp = properties[0];

        if (firstProp.Value.ValueKind == JsonValueKind.Number)
        {
            // Mapped format: {"a": 1.0, "b": 2.0}
            return ParseMapped(properties, valueType, tensorType);
        }

        if (firstProp.Value.ValueKind == JsonValueKind.Array)
        {
            // Mixed single sparse: {"x1": [1.0, 2.0], "x2": [3.0, 4.0]}
            return ParseMixedSingleSparse(properties, valueType, tensorType);
        }

        throw new JsonException("Unable to determine tensor format");
    }

    private static VespaTensor ParseMapped(List<JsonProperty> properties, Type valueType, string? tensorType)
    {
        if (valueType == typeof(float))
        {
            var dict = properties.ToDictionary(p => p.Name, p => p.Value.GetSingle());
            return VespaTensor.FromMappedValues(dict, tensorType);
        }

        if (valueType == typeof(sbyte))
        {
            var dict = properties.ToDictionary(p => p.Name, p => (sbyte)p.Value.GetDouble());
            return VespaTensor.FromMappedValues(dict, tensorType);
        }

        if (valueType == typeof(Half))
        {
            var dict = properties.ToDictionary(p => p.Name, p => (Half)p.Value.GetSingle());
            return VespaTensor.FromMappedValues(dict, tensorType);
        }
        else
        {
            var dict = properties.ToDictionary(p => p.Name, p => p.Value.GetDouble());
            return VespaTensor.FromMappedValues(dict, tensorType);
        }
    }

    private static VespaTensor ParseMixedSingleSparse(List<JsonProperty> properties, Type valueType, string? tensorType)
    {
        if (valueType == typeof(float))
        {
            var dict = properties.ToDictionary(
                p => p.Name,
                p => p.Value.EnumerateArray().Select(e => e.GetSingle()).ToArray()
            );
            return VespaTensor.FromMixedSingleSparse(dict, tensorType);
        }

        if (valueType == typeof(sbyte))
        {
            var dict = properties.ToDictionary(
                p => p.Name,
                p => p.Value.EnumerateArray().Select(e => (sbyte)e.GetDouble()).ToArray()
            );
            return VespaTensor.FromMixedSingleSparse(dict, tensorType);
        }

        if (valueType == typeof(Half))
        {
            var dict = properties.ToDictionary(
                p => p.Name,
                p => p.Value.EnumerateArray().Select(e => (Half)e.GetSingle()).ToArray()
            );
            return VespaTensor.FromMixedSingleSparse(dict, tensorType);
        }
        else
        {
            var dict = properties.ToDictionary(
                p => p.Name,
                p => p.Value.EnumerateArray().Select(e => e.GetDouble()).ToArray()
            );
            return VespaTensor.FromMixedSingleSparse(dict, tensorType);
        }
    }

    private static VespaTensor ParseBlocks(JsonElement blocksElem, Type valueType, string? tensorType)
    {
        var blocks = new List<TensorBlock>();

        foreach (var blockElem in blocksElem.EnumerateArray())
        {
            var block = new TensorBlock();

            if (blockElem.TryGetProperty("address", out var addrElem))
            {
                block.Address = addrElem.EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
            }

            if (blockElem.TryGetProperty("values", out var valuesElem))
            {
                if (valueType == typeof(float))
                    block.SetValues(valuesElem.EnumerateArray().Select(e => e.GetSingle()).ToArray());
                else if (valueType == typeof(sbyte))
                    block.SetValues(valuesElem.EnumerateArray().Select(e => (sbyte)e.GetDouble()).ToArray());
                else if (valueType == typeof(Half))
                    block.SetValues(valuesElem.EnumerateArray().Select(e => (Half)e.GetSingle()).ToArray());
                else
                    block.SetValues(valuesElem.EnumerateArray().Select(e => e.GetDouble()).ToArray());
            }

            blocks.Add(block);
        }

        return VespaTensor.FromBlocks(blocks, tensorType);
    }

    private static TensorCell ReadTensorCell(ref Utf8JsonReader reader, Type valueType)
    {
        var cell = new TensorCell();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var propertyName = reader.GetString();
            reader.Read();

            switch (propertyName)
            {
                case "address":
                    if (reader.TokenType == JsonTokenType.StartObject)
                        cell.Address = ReadStringDictionary(ref reader);
                    break;

                case "value":
                    if (reader.TokenType == JsonTokenType.Number)
                    {
                        if (valueType == typeof(float))
                            cell.Value = reader.GetSingle();
                        else if (valueType == typeof(sbyte))
                            cell.Value = (sbyte)reader.GetDouble();
                        else if (valueType == typeof(Half))
                            cell.Value = (Half)reader.GetSingle();
                        else
                            cell.Value = reader.GetDouble();
                    }
                    break;

                default:
                    reader.Skip();
                    break;
            }
        }

        return cell;
    }

    private static Dictionary<string, string> ReadStringDictionary(ref Utf8JsonReader reader)
    {
        var dict = new Dictionary<string, string>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var key = reader.GetString();
            reader.Read();

            if (reader.TokenType == JsonTokenType.String && key != null)
                dict[key] = reader.GetString() ?? string.Empty;
            else
                reader.Skip();
        }

        return dict;
    }

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

        // Try different types
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

        writer.WriteEndObject();
    }

    private static void WriteMixedSingleSparse(Utf8JsonWriter writer, VespaTensor tensor)
    {
        if (tensor.InternalMixedSingleSparse == null)
            throw new JsonException("InternalMixedSingleSparse is null for MixedSingleSparse format");

        writer.WriteStartObject();

        // Try different types
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
                writer.WriteNumberValue(Convert.ToDouble(value));
                break;
        }
    }
}
