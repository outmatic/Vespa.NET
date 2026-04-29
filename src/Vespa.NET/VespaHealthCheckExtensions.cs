using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Vespa;

/// <summary>
/// Extension methods for registering Vespa health checks with ASP.NET Core.
/// </summary>
public static class VespaHealthCheckExtensions
{
    /// <summary>
    /// Adds a Vespa readiness health check that calls <see cref="IVespaClient.IsReadyAsync"/>.
    /// </summary>
    public static IHealthChecksBuilder AddVespa(
        this IHealthChecksBuilder builder,
        string name = "vespa",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new VespaHealthCheck(sp.GetRequiredService<IVespaClient>()),
            failureStatus,
            tags));
    }
}
