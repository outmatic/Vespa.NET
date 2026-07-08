using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Custom JSON converter for Vespa embedding fields (float[] only)
/// For full tensor support with all formats, use VespaTensorConverter with VespaTensor type.
/// This converter provides backward compatibility and convenience for simple float[] embeddings.
/// Supports: array format [1.0, 2.0], object format {"values": [1.0, 2.0]} or {"cells": [1.0, 2.0]},
/// and hex-encoded dense values (<c>presentation.format.tensors=hex|hex-value</c>), which are
/// always decoded as float32 — 8 hex digits per value.
/// </summary>
public sealed class VespaEmbeddingConverter : JsonConverter<float[]?>
{
    public override float[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.StartArray => ReadArray(ref reader),
            JsonTokenType.StartObject => ReadObject(ref reader),
            // presentation.format.tensors=hex-value renders dense tensors as a bare hex string
            JsonTokenType.String => ParseHexFloats(ref reader),
            _ => throw new JsonException($"Unexpected token type: {reader.TokenType}")
        };

    private static float[]? ReadObject(ref Utf8JsonReader reader)
    {
        float[]? result = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.GetString() is "values" or "cells")
            {
                reader.Read();
                result = reader.TokenType switch
                {
                    JsonTokenType.StartArray => ReadArray(ref reader),
                    // Short form mapped cells: {"cells":{"a":1.0,"b":2.0}}
                    JsonTokenType.StartObject => ReadMappedCells(ref reader),
                    // Hex-encoded dense values: {"values":"3F8000004000000..."}
                    JsonTokenType.String => ParseHexFloats(ref reader),
                    _ => SkipAndKeep(ref reader, result)
                };
            }
            else
            {
                reader.Skip();
            }
        }

        return result;
    }

    private static float[]? SkipAndKeep(ref Utf8JsonReader reader, float[]? current)
    {
        reader.Skip();
        return current;
    }

    private static float[] ReadArray(ref Utf8JsonReader reader)
    {
        var list = new List<float>();
        ReadArrayInto(ref reader, list);
        return list.ToArray();
    }

    private static void ReadArrayInto(ref Utf8JsonReader reader, List<float> output)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    output.Add(reader.GetSingle());
                    break;
                // Multi-dimensional short form — flatten row-major
                case JsonTokenType.StartArray:
                    ReadArrayInto(ref reader, output);
                    break;
                // Long-form cell: {"address":{...},"value":2.0}
                case JsonTokenType.StartObject:
                    ReadCellObjectInto(ref reader, output);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
    }

    private static void ReadCellObjectInto(ref Utf8JsonReader reader, List<float> output)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.GetString() == "value")
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.Number)
                    output.Add(reader.GetSingle());
                else
                    reader.Skip();
            }
            else
            {
                reader.Skip();
            }
        }
    }

    /// <summary>
    /// Decodes the documented hex string form for dense tensor values. Since this
    /// converter has no access to the tensor type, values are always decoded as
    /// float32 (8 hex digits per value, big-endian bit pattern) — the only cell
    /// type this converter supports. Use <c>VespaTensor</c> for other cell types.
    /// </summary>
    private static float[] ParseHexFloats(ref Utf8JsonReader reader)
    {
        var hex = reader.GetString()!;
        if (hex.Length % 8 != 0)
            throw new JsonException(
                $"Hex embedding value length {hex.Length} is not a multiple of 8 hex digits per float32 value");

        var result = new float[hex.Length / 8];
        for (var i = 0; i < result.Length; i++)
        {
            if (!uint.TryParse(hex.AsSpan(i * 8, 8),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var bits))
                throw new JsonException($"Invalid hex digits in embedding value: '{hex.Substring(i * 8, 8)}'");
            result[i] = BitConverter.UInt32BitsToSingle(bits);
        }
        return result;
    }

    /// <summary>
    /// Reads short-form mapped cells ({"cells":{"a":1.0,"b":2.0}}).
    /// Note: the mapped dimension labels are discarded — values are returned
    /// positionally in JSON document order, which is only meaningful when the
    /// caller does not depend on specific labels. Use <c>VespaTensor</c> to
    /// preserve the address of each cell.
    /// </summary>
    private static float[] ReadMappedCells(ref Utf8JsonReader reader)
    {
        var list = new List<float>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            reader.Read();
            if (reader.TokenType == JsonTokenType.Number)
                list.Add(reader.GetSingle());
            else
                reader.Skip();
        }
        return list.ToArray();
    }

    public override void Write(Utf8JsonWriter writer, float[]? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value)
            writer.WriteNumberValue(item);

        writer.WriteEndArray();
    }
}
