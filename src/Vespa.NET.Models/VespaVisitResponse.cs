using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Represents a single page of results from the Vespa visit/iterate API
/// </summary>
public sealed record VespaVisitResponse<T>(
    [property: JsonPropertyName("documents")] List<VespaDocument<T>> Documents,
    [property: JsonPropertyName("continuation")] string? Continuation,
    [property: JsonPropertyName("documentCount")] int DocumentCount,
    [property: JsonPropertyName("pathId")] string? PathId
) where T : class;
