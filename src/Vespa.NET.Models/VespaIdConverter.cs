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

    private static string GetShortId(string fullId) => VespaDocumentId.GetUserSpecified(fullId);
}
