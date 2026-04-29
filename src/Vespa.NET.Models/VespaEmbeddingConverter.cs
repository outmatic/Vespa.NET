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
            if (reader.TokenType == JsonTokenType.PropertyName && reader.GetString() is "values" or "cells")
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartArray)
                    result = ReadArray(ref reader);
            }
            else
                reader.Skip();

        return result;
    }

    private static float[] ReadArray(ref Utf8JsonReader reader)
    {
        var list = new List<float>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            if (reader.TokenType == JsonTokenType.Number)
                list.Add(reader.GetSingle());

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
