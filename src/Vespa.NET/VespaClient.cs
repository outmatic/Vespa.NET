using System.Net.Http.Json;
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
/// VespaClient requires an HttpClient instance from IHttpClientFactory.
/// Use the AddVespaClient extension method in VespaServiceCollectionExtensions to configure it properly.
/// Resilience strategies (retry, circuit breaker) should be configured at the IHttpClientFactory level.
/// </remarks>
public sealed partial class VespaClient : IVespaClient
{
    private readonly HttpClient _httpClient;
    private readonly VespaClientOptions _options;
    private readonly ILogger<VespaClient>? _logger;
    private readonly Lazy<AdminOperations> _admin;

    public IDocumentOperations Documents { get; }
    public ISearchOperations Search { get; }
    public IFeedOperations Feed { get; }
    public IAdminOperations Admin => _admin.Value;

    /// <summary>
    /// Create a new VespaClient with an HttpClient from IHttpClientFactory
    /// </summary>
    /// <param name="httpClient">HttpClient instance (use IHttpClientFactory for production)</param>
    /// <param name="options">VespaClient configuration options</param>
    /// <param name="logger">Optional logger instance</param>
    /// <remarks>
    /// Configure resilience strategies (retry, circuit breaker) at the service collection level
    /// using AddVespaClient extension methods.
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
    /// the client is activated.
    /// </summary>
    internal VespaClient(
        HttpClient httpClient,
        VespaClientOptions options,
        ILogger<VespaClient>? logger,
        bool httpClientPreconfigured)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _logger = logger;

        // Apply common defaults for the non-DI path only. The DI path already ran
        // ConfigureHttpClientDefaults plus the user's configureHttpClient callback —
        // re-applying defaults here would clobber user customizations (Timeout,
        // headers, …) and duplicate options.DefaultRequestHeaders.
        if (!httpClientPreconfigured)
            ConfigureDirectHttpClient(_httpClient, options.Endpoint, options);

        // The config-server client (port 19071) gets its own SocketsHttpHandler and
        // an mTLS certificate loaded from disk — created lazily on first Admin access:
        // most instances (the DI typed client is transient; health checks construct
        // one per probe) never deploy schemas, and building a handler + reloading the
        // certificate per instance would churn connection pools for nothing.
        _admin = new Lazy<AdminOperations>(() =>
        {
            var configEndpoint = options.ConfigServerEndpoint ?? DeriveConfigEndpoint(options.Endpoint);
            var handler = VespaServiceCollectionExtensions.BuildSocketsHttpHandler(options);
            var adminHttpClient = new HttpClient(handler, disposeHandler: true);
            ConfigureDirectHttpClient(adminHttpClient, configEndpoint, options);
            return new AdminOperations(adminHttpClient, options, logger);
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        // Initialize operation handlers
        Documents = new DocumentOperations(_httpClient, options, logger);
        Search = new SearchOperations(_httpClient, options, logger);
        Feed = new FeedOperations(Documents, options, logger);

        if (_logger != null)
            LogInitialized(_logger, _options.Endpoint);
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

                using var doc = System.Text.Json.JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(ct));

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

    private async Task<T?> TryProbeAsync<T>(
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
                using var doc = System.Text.Json.JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(cancellationToken));
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

    private static void ConfigureDirectHttpClient(
        HttpClient httpClient,
        string endpoint,
        VespaClientOptions options)
    {
        var existingAuthorization = httpClient.DefaultRequestHeaders.Authorization;
        VespaServiceCollectionExtensions.ConfigureHttpClientDefaults(httpClient, endpoint, options);

        if (existingAuthorization is not null)
            httpClient.DefaultRequestHeaders.Authorization = existingAuthorization;
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
        // Do NOT dispose _httpClient — it's externally owned (IHttpClientFactory).
        // Dispose the admin HttpClient we created internally via AdminOperations,
        // but only if it was ever materialized.
        if (_admin.IsValueCreated)
            _admin.Value.Dispose();
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
}
