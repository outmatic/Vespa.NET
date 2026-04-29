using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Represents a Vespa document with typed fields
/// </summary>
/// <typeparam name="T">Type of the document fields</typeparam>
public sealed record VespaDocument<T> where T : class
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(VespaIdConverter))]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("fields")]
    public T? Fields { get; init; }

    [JsonPropertyName("pathId")]
    public string? PathId { get; init; }
}
