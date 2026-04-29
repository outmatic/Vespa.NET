using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Represents a Vespa error response
/// </summary>
public sealed record VespaError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("details")]
    public string? Details { get; init; }

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; init; }
}
