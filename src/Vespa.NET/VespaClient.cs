using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Vespa.Admin;
using Vespa.Documents;
using Vespa.Feed;
using Vespa.Models;
using Vespa.Search;

namespace Vespa;

/// <summary>
/// Main VespaClient implementation
/// </summary>
/// <remarks>
/// Prefer registering the client via the AddVespaClient extension methods in
/// <see cref="VespaServiceCollectionExtensions"/>: only that path installs the resilience
/// pipeline (retry, circuit breaker) and pools handlers through IHttpClientFactory.
/// For DI-less scenarios, the <see cref="VespaClient(VespaClientOptions, ILogger{VespaClient})"/>
/// constructor builds its own fully configured <see cref="HttpClient"/> (mTLS, compression,
/// pooling) from the options.
/// </remarks>
public sealed partial class VespaClient : IVespaClient
{
    private readonly HttpClient _httpClient;
    private readonly VespaClientOptions _options;
    private readonly ILogger<VespaClient>? _logger;
    private readonly Lazy<AdminOperations> _admin;
    private readonly bool _ownsHttpClient;
    private readonly X509Certificate2? _ownedCertificate;
    private X509Certificate2? _adminOwnedCertificate;
    private readonly object _adminLock = new();
    private bool _disposed;

    public IDocumentOperations Documents { get; }
    public ISearchOperations Search { get; }
    public IFeedOperations Feed { get; }

    /// <remarks>
    /// The admin (config-server) <see cref="HttpClient"/> is created lazily per
    /// <see cref="VespaClient"/> instance and disposed with it. For transient/scoped
    /// consumers that deploy schemas frequently, resolve a longer-lived client (or reuse
    /// one instance) to avoid rebuilding the admin connection pool on every scope.
    /// </remarks>
    public IAdminOperations Admin
    {
        get
        {
            // The lock makes materialization atomic with Dispose: without it a
            // concurrent Dispose could observe IsValueCreated == false while the
            // factory is still running and leak the admin HttpClient + handler.
            lock (_adminLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _admin.Value;
            }
        }
    }

    /// <summary>
    /// Creates a self-contained VespaClient that builds and owns its own
    /// <see cref="HttpClient"/> from <paramref name="options"/> — including the mTLS
    /// client certificate, compression, and connection-pool settings.
    /// </summary>
    /// <param name="options">VespaClient configuration options</param>
    /// <param name="logger">Optional logger instance</param>
    /// <remarks>
    /// HTTP-level resilience (<see cref="VespaClientOptions.EnableRetry"/>,
    /// <see cref="VespaClientOptions.EnableCircuitBreaker"/>) is only installed by the DI
    /// registrations (<c>AddVespaClient</c>); this constructor applies feed-level retry only
    /// and logs a warning when those options are set.
    /// </remarks>
    public VespaClient(VespaClientOptions options, ILogger<VespaClient>? logger = null)
        : this(CreateOwnedHttpClient(options, out var ownedCertificate), options, logger,
            httpClientPreconfigured: true, ownsHttpClient: true, ownedCertificate: ownedCertificate)
    {
    }

    /// <summary>
    /// Create a new VespaClient over a caller-supplied HttpClient
    /// </summary>
    /// <param name="httpClient">HttpClient instance (use IHttpClientFactory for production)</param>
    /// <param name="options">VespaClient configuration options</param>
    /// <param name="logger">Optional logger instance</param>
    /// <remarks>
    /// <para><b>This constructor mutates <paramref name="httpClient"/>:</b> it sets
    /// <c>BaseAddress</c>, <c>Timeout</c>, the HTTP version policy, and default request
    /// headers (including <c>Authorization</c> from <see cref="VespaClientOptions.ApiKey"/>)
    /// from <paramref name="options"/>. Do not share the instance with non-Vespa callers.</para>
    /// <para>Options that live on the HTTP handler cannot be honored here and throw
    /// <see cref="InvalidOperationException"/> when set (mTLS certificates) — configure the
    /// handler yourself via <see cref="VespaServiceCollectionExtensions.BuildSocketsHttpHandler(VespaClientOptions)"/>,
    /// use the <see cref="VespaClient(VespaClientOptions, ILogger{VespaClient})"/> constructor,
    /// or register through DI. HTTP-level retry/circuit-breaker options are only installed by
    /// the DI registrations and log a warning here when set.</para>
    /// </remarks>
    public VespaClient(
        HttpClient httpClient,
        VespaClientOptions options,
        ILogger<VespaClient>? logger = null)
        : this(httpClient, options, logger, httpClientPreconfigured: false)
    {
    }

    /// <summary>
    /// Internal constructor used by the DI registrations, which configure the
    /// <see cref="HttpClient"/> in the factory (defaults + user callback) before
    /// the client is activated, and by the options-only constructor
    /// (<paramref name="ownsHttpClient"/>).
    /// </summary>
    internal VespaClient(
        HttpClient httpClient,
        VespaClientOptions options,
        ILogger<VespaClient>? logger,
        bool httpClientPreconfigured,
        bool ownsHttpClient = false,
        X509Certificate2? ownedCertificate = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _logger = logger;
        _ownsHttpClient = ownsHttpClient;
        _ownedCertificate = ownedCertificate;

        // Apply common defaults for the caller-supplied-HttpClient path only. The DI
        // path already ran ConfigureHttpClientDefaults plus the user's
        // configureHttpClient callback, and the owned path configured the client it
        // built — re-applying defaults would clobber customizations (Timeout,
        // headers, …) and duplicate options.DefaultRequestHeaders.
        if (!httpClientPreconfigured)
        {
            // mTLS lives on the HTTP handler; it cannot be attached to a caller-built client.
            if (options.CertificatePath is not null || options.ClientCertificate is not null)
                throw new InvalidOperationException(
                    "A client certificate is configured (CertificatePath/ClientCertificate), but it cannot " +
                    "be attached to a caller-supplied HttpClient. Build the handler via " +
                    "VespaServiceCollectionExtensions.BuildSocketsHttpHandler(options), use the " +
                    "VespaClient(VespaClientOptions) constructor, or register the client with AddVespaClient.");

            // A pre-set Authorization header would silently win over options.ApiKey.
            if (!string.IsNullOrEmpty(options.ApiKey) && _httpClient.DefaultRequestHeaders.Authorization is not null)
                throw new InvalidOperationException(
                    "Options.ApiKey is set, but the supplied HttpClient already has an Authorization default " +
                    "header. Remove one of the two — the client cannot decide which credential should win.");

            VespaServiceCollectionExtensions.ConfigureHttpClientDefaults(_httpClient, options.Endpoint, options);
        }

        // Directly constructed clients have no Polly resilience handler.
        if (!httpClientPreconfigured || ownsHttpClient)
            if (_logger != null && (options.EnableRetry || options.EnableCircuitBreaker))
                LogResilienceNotEnforced(_logger);

        // The config-server client (port 19071) gets its own SocketsHttpHandler and
        // an mTLS certificate loaded from disk — created lazily on first Admin access:
        // most instances (the DI typed client is transient; health checks construct
        // one per probe) never deploy schemas, and building a handler + reloading the
        // certificate per instance would churn connection pools for nothing.
        _admin = new Lazy<AdminOperations>(() =>
        {
            var configEndpoint = options.ConfigServerEndpoint ?? DeriveConfigEndpoint(options.Endpoint);
            var handler = VespaServiceCollectionExtensions.BuildSocketsHttpHandler(options, out _adminOwnedCertificate);
            var adminHttpClient = new HttpClient(handler, disposeHandler: true);
            VespaServiceCollectionExtensions.ConfigureHttpClientDefaults(adminHttpClient, configEndpoint, options);
            return new AdminOperations(adminHttpClient, options, logger, ownsHttpClient: true);
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        // Initialize operation handlers
        Documents = new DocumentOperations(_httpClient, options, logger);
        Search = new SearchOperations(_httpClient, options, logger);
        Feed = new FeedOperations(Documents, options, logger);

        if (_logger != null)
            LogInitialized(_logger, _options.Endpoint);
    }

    private static HttpClient CreateOwnedHttpClient(
        VespaClientOptions options,
        out X509Certificate2? ownedCertificate)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var handler = VespaServiceCollectionExtensions.BuildSocketsHttpHandler(options, out ownedCertificate);
        try
        {
            var httpClient = new HttpClient(handler, disposeHandler: true);
            VespaServiceCollectionExtensions.ConfigureHttpClientDefaults(httpClient, options.Endpoint, options);
            return httpClient;
        }
        catch
        {
            handler.Dispose();
            ownedCertificate?.Dispose();
            throw;
        }
    }

    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
        => await TryProbeAsync(
            async ct =>
            {
                using var response = await _httpClient.GetAsync(VespaPaths.Health, HttpCompletionOption.ResponseHeadersRead, ct);
                var isHealthy = response.IsSuccessStatusCode;

                if (_logger != null)
                    LogHealthCheck(_logger, isHealthy ? "Healthy" : "Unhealthy");

                return isHealthy;
            },
            ex =>
            {
                if (_logger != null)
                    LogHealthCheckFailed(_logger, ex);
                return false;
            },
            cancellationToken);

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
        => await TryProbeAsync(
            async ct =>
            {
                using var response = await _httpClient.GetAsync(VespaPaths.Health, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) return false;

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);

                return doc.RootElement.TryGetProperty("status", out var status) &&
                       status.TryGetProperty("code", out var code) &&
                       code.GetString() == "up";
            },
            _ => false,
            cancellationToken);

    public async Task<VespaMetrics?> GetMetricsAsync(CancellationToken cancellationToken = default)
        => await TryProbeAsync(
            ct => GetJsonOrDefaultAsync<VespaMetrics>(VespaPaths.Metrics, ct),
            ex =>
            {
                if (_logger != null)
                    LogMetricsError(_logger, ex);
                return null;
            },
            cancellationToken);

    private static async Task<T?> TryProbeAsync<T>(
        Func<CancellationToken, Task<T?>> operation,
        Func<Exception, T?> onFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation(cancellationToken);
        }
        // Rethrow only the caller's cancellation: HttpClient timeouts surface as
        // TaskCanceledException too, and those must count as a failed probe.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return onFailure(ex);
        }
    }

    public Task<VespaStateConfig?> GetConfigAsync(CancellationToken cancellationToken = default) =>
        GetOrDefaultAsync(VespaPaths.Config,
            response => response.Content.ReadFromJsonAsync<VespaStateConfig>(VespaJsonOptions.Default, cancellationToken),
            cancellationToken);

    public Task<string?> GetVersionAsync(CancellationToken cancellationToken = default) =>
        GetOrDefaultAsync(VespaPaths.Version,
            async response =>
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            },
            cancellationToken);

    public Task<string?> GetHistogramsAsync(CancellationToken cancellationToken = default) =>
        GetOrDefaultAsync(VespaPaths.Histograms,
            async response => (string?)await response.Content.ReadAsStringAsync(cancellationToken),
            cancellationToken);

    private async Task<T?> GetOrDefaultAsync<T>(
        string path,
        Func<HttpResponseMessage, Task<T?>> parseResponse,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return default;
            return await parseResponse(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return default;
        }
    }

    private async Task<T?> GetJsonOrDefaultAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<T>(VespaJsonOptions.Default, cancellationToken);

        if (_logger != null)
            LogMetricsFailed(_logger, response.StatusCode);
        return default;
    }

    public void Dispose()
    {
        // Dispose only what this instance created: the lazily-materialized admin
        // HttpClient (+ its certificate loaded from disk) and, for the options-only
        // constructor, the owned main HttpClient. A caller-supplied or factory-owned
        // HttpClient is never disposed here. Taken under _adminLock so a concurrent
        // Admin materialization cannot slip past IsValueCreated and leak.
        lock (_adminLock)
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_admin.IsValueCreated)
                _admin.Value.Dispose();
            _adminOwnedCertificate?.Dispose();

            if (_ownsHttpClient)
                _httpClient.Dispose();
            _ownedCertificate?.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static string DeriveConfigEndpoint(string queryEndpoint) =>
        new UriBuilder(queryEndpoint) { Port = 19071 }.Uri.ToString();

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "VespaClient initialized for endpoint {Endpoint}")]
    static partial void LogInitialized(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Health check completed: {Status}")]
    static partial void LogHealthCheck(ILogger logger, string status);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Health check failed")]
    static partial void LogHealthCheckFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to get metrics: {StatusCode}")]
    static partial void LogMetricsFailed(ILogger logger, System.Net.HttpStatusCode statusCode);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Failed to get metrics")]
    static partial void LogMetricsError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "EnableRetry/EnableCircuitBreaker are set, but HTTP-level resilience is only installed by the " +
                  "DI registrations (AddVespaClient). This directly constructed client applies feed-level retry only.")]
    static partial void LogResilienceNotEnforced(ILogger logger);
}
