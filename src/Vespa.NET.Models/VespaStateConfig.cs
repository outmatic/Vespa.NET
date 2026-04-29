using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Config generation returned by <c>/state/v1/config</c>.
/// </summary>
public sealed record VespaStateConfig(
    [property: JsonPropertyName("config")] VespaConfigGeneration Config
);

/// <summary>
/// Configuration generation details.
/// </summary>
public sealed record VespaConfigGeneration(
    [property: JsonPropertyName("generation")] long Generation
);
