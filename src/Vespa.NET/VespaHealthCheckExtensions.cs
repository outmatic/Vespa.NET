using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Vespa;

/// <summary>
/// Extension methods for registering Vespa health checks with ASP.NET Core.
/// </summary>
public static class VespaHealthCheckExtensions
{
    /// <summary>
    /// Adds a Vespa readiness health check that calls <see cref="IVespaClient.IsReadyAsync"/>
    /// on the default (unnamed) <see cref="IVespaClient"/> registration.
    /// </summary>
    public static IHealthChecksBuilder AddVespa(
        this IHealthChecksBuilder builder,
        string name = "vespa",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new VespaHealthCheck(sp.GetRequiredService<IVespaClient>()),
            failureStatus,
            tags));
    }

    /// <summary>
    /// Adds a Vespa readiness health check for a named client registered via
    /// <c>AddVespaClient(name, options)</c>, resolved as a keyed <see cref="IVespaClient"/>.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="clientName">The logical name the Vespa client was registered under.</param>
    /// <param name="name">The health check name (must be unique across health checks).</param>
    /// <param name="failureStatus">Status reported on failure; defaults to <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="tags">Optional health check tags.</param>
    public static IHealthChecksBuilder AddVespa(
        this IHealthChecksBuilder builder,
        string clientName,
        string name,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return builder.Add(new HealthCheckRegistration(
            name,
            sp => new VespaHealthCheck(sp.GetRequiredKeyedService<IVespaClient>(clientName)),
            failureStatus,
            tags));
    }
}
