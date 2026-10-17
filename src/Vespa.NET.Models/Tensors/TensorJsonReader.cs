using System.Text.Json;

namespace Vespa.Models.Tensors;

/// <summary>Reads the documented JSON forms of Vespa tensors.</summary>
internal static class TensorJsonReader
{
    internal static VespaTensor? Read(ref Utf8JsonReader reader, string? tensorType)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => FromDenseArray(
                TensorHexDecoder.Decode(reader.GetString()!, tensorType, VespaTensor.DetectValueType(tensorType)), tensorType),
            JsonTokenType.StartArray => ReadArrayFormat(ref reader, tensorType),
            JsonTokenType.StartObject => ReadObjectFormat(ref reader, tensorType),
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

    private static VespaTensor ReadObjectFormat(ref Utf8JsonReader reader, string? declaredType)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        // In value-only forms, "type", "values", "cells" and "blocks" can be
        // mapped labels. A string type specification identifies an actual wrapper.
        var hasType = root.TryGetProperty("type", out var typeElem) && typeElem.ValueKind == JsonValueKind.String &&
            typeElem.GetString()!.StartsWith("tensor", StringComparison.Ordinal);
        var tensorType = hasType ? typeElem.GetString() : declaredType;
        var valueType = VespaTensor.DetectValueType(tensorType);
        var properties = root.EnumerateObject().Where(p => !hasType || p.Name != "type").ToList();

        if (!hasType)
        {
            if (properties.Count > 0 && properties.All(p => p.Value.ValueKind == JsonValueKind.Number))
                return ParseMapped(properties, valueType, tensorType);

            // A declared mixed tensor disambiguates {"values":[...]} (a mapped
            // label) from the dense wrapper with the same spelling.
            if (tensorType is not null && tensorType.Count(c => c == '{') == 1 && tensorType.Contains('[') &&
                properties.All(p => IsDenseBlockValue(p.Value)))
                return ParseMixedSingleSparse(properties, valueType, tensorType);
        }

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
        return ParseMappedOrMixed(properties, valueType, tensorType);
    }

    private static bool IsDenseBlockValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ||
        (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(e => e.ValueKind is JsonValueKind.Number or JsonValueKind.Array));

    private static VespaTensor ParseCellsProperty(JsonElement cellsElem, Type valueType, string? tensorType)
    {
        // Short form mapped: {"cells":{"a":2.0}}
        if (cellsElem.ValueKind == JsonValueKind.Object)
            return ParseMapped(cellsElem.EnumerateObject().ToList(), valueType, tensorType);

        // Hex-encoded dense values (presentation.format.tensors=hex)
        if (cellsElem.ValueKind == JsonValueKind.String)
            return FromDenseArray(TensorHexDecoder.Decode(cellsElem.GetString()!, tensorType, valueType), tensorType);

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
            return TensorHexDecoder.Decode(element.GetString()!, tensorType, valueType);

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

    private static VespaTensor ParseMappedOrMixed(List<JsonProperty> properties, Type valueType, string? tensorType)
    {
        if (properties.Count == 0)
            return tensorType is not null && tensorType.Contains('[') && !tensorType.Contains('{')
                ? CreateEmptyDenseTensor(valueType, tensorType)
                : CreateEmptyMappedTensor(valueType, tensorType);

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
    /// Converts a JSON-rendered cell value (Vespa renders int8 cells as doubles, e.g. 2.0)
    /// to <see cref="sbyte"/>, failing loudly instead of saturating/truncating.
    /// </summary>
    private static sbyte ToInt8(double value) =>
        double.IsInteger(value) && value is >= sbyte.MinValue and <= sbyte.MaxValue
            ? (sbyte)value
            : throw new JsonException($"Value {value} is not a valid int8 tensor cell (integer in [-128, 127] expected).");
}
