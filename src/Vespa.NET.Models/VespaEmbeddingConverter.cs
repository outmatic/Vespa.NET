using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Custom JSON converter for Vespa embedding fields (float[] only)
/// For full tensor support with all formats, use VespaTensorConverter with VespaTensor type.
/// This converter provides backward compatibility and convenience for simple float[] embeddings.
/// Supports: array format [1.0, 2.0] and object format {"values": [1.0, 2.0]} or {"cells": [1.0, 2.0]}
/// </summary>
public sealed class VespaEmbeddingConverter : JsonConverter<float[]?>
{
    public override float[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.StartArray => ReadArray(ref reader),
            JsonTokenType.StartObject => ReadObject(ref reader),
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
