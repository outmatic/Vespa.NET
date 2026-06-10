using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Represents a single page of results from the Vespa visit/iterate API
/// </summary>
public sealed record VespaVisitResponse<T>(
    List<VespaDocument<T>>? Documents,
    [property: JsonPropertyName("continuation")] string? Continuation,
    [property: JsonPropertyName("documentCount")] int DocumentCount,
    [property: JsonPropertyName("pathId")] string? PathId
) where T : class
{
    /// <summary>Documents in this page; empty when the response carried none (no NREs on empty pages).</summary>
    [JsonPropertyName("documents")]
    public List<VespaDocument<T>> Documents { get; init; } = Documents ?? [];
}
