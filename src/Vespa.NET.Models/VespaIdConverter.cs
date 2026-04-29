using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Custom converter to automatically shorten Vespa IDs during deserialization
/// </summary>
public sealed class VespaIdConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var fullId = reader.GetString();
        return fullId != null ? GetShortId(fullId) : null;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }

    private static string GetShortId(string fullId)
    {
        if (string.IsNullOrEmpty(fullId)) return fullId;

        var doubleColonIndex = fullId.LastIndexOf("::", StringComparison.Ordinal);
        if (doubleColonIndex >= 0)
        {
            return fullId[(doubleColonIndex + 2)..];
        }

        var lastColonIndex = fullId.LastIndexOf(':');
        if (lastColonIndex >= 0)
        {
            return fullId[(lastColonIndex + 1)..];
        }

        return fullId;
    }
}
