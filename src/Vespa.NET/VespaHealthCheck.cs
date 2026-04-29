using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Vespa;

/// <summary>
/// ASP.NET Core <see cref="IHealthCheck"/> implementation that checks Vespa readiness
/// via <see cref="IVespaClient.IsReadyAsync"/>.
/// </summary>
public sealed class VespaHealthCheck(IVespaClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var isReady = await client.IsReadyAsync(cancellationToken);

        return isReady
            ? HealthCheckResult.Healthy("Vespa cluster is ready")
            : HealthCheckResult.Unhealthy("Vespa cluster is not ready");
    }
}
