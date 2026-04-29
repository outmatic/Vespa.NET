using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Application status returned by the Vespa config-server
/// at <c>/application/v2/tenant/default/application/default</c>.
/// </summary>
public sealed record VespaApplicationStatus(
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("application")] string Application,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("active")] bool IsActive
);
