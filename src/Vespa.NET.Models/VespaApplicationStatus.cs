using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Application status returned by the Vespa config-server at
/// <c>/application/v2/tenant/{tenant}/application/default</c>.
/// The endpoint returns the config generation of the active application.
/// </summary>
public sealed record VespaApplicationStatus(
    [property: JsonPropertyName("generation")] long Generation
);
