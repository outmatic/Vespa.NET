using Vespa.Admin;
using Vespa.Documents;
using Vespa.Feed;
using Vespa.Models;
using Vespa.Search;

namespace Vespa;

/// <summary>
/// Main interface for VespaClient
/// </summary>
public interface IVespaClient : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Document operations (CRUD)
    /// </summary>
    IDocumentOperations Documents { get; }

    /// <summary>
    /// Search operations
    /// </summary>
    ISearchOperations Search { get; }

    /// <summary>
    /// Bulk feed operations
    /// </summary>
    IFeedOperations Feed { get; }

    /// <summary>
    /// Administrative operations (deploy schemas, check application status).
    /// Targets the Vespa config server (port 19071 by default).
    /// </summary>
    IAdminOperations Admin { get; }

    /// <summary>
    /// Liveness check — returns <see langword="true"/> when Vespa responds with HTTP 2xx.
    /// Use for process-level liveness probes (k8s <c>livenessProbe</c>).
    /// Propagates <see cref="OperationCanceledException"/> when the request is cancelled.
    /// </summary>
    Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Readiness check — returns <see langword="true"/> only when Vespa reports
    /// <c>status.code == "up"</c> in <c>/state/v1/health</c>.
    /// Use for k8s <c>readinessProbe</c> to gate traffic until the cluster is fully initialized.
    /// Propagates <see cref="OperationCanceledException"/> when the request is cancelled.
    /// </summary>
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get cluster metrics from <c>/state/v1/metrics</c>.
    /// Returns <see langword="null"/> on HTTP error or deserialization failure.
    /// Propagates <see cref="OperationCanceledException"/> when the request is cancelled.
    /// </summary>
    Task<VespaMetrics?> GetMetricsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the config generation from <c>/state/v1/config</c>.
    /// Returns <see langword="null"/> on HTTP error.
    /// Propagates <see cref="OperationCanceledException"/> when the request is cancelled.
    /// </summary>
    Task<VespaStateConfig?> GetConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the Vespa version string from <c>/state/v1/version</c>.
    /// Returns <see langword="null"/> on HTTP error.
    /// Propagates <see cref="OperationCanceledException"/> when the request is cancelled.
    /// </summary>
    Task<string?> GetVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get HdrHistogram data from <c>/state/v1/metrics/histograms</c>.
    /// Returns raw CSV/text content, or <see langword="null"/> on error.
    /// Propagates <see cref="OperationCanceledException"/> when the request is cancelled.
    /// </summary>
    Task<string?> GetHistogramsAsync(CancellationToken cancellationToken = default);
}
