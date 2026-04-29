using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly;

namespace Vespa;

/// <summary>
/// Extension methods for configuring VespaClient with IHttpClientFactory
/// </summary>
public static partial class VespaServiceCollectionExtensions
{
    private static readonly HttpRetryStrategyOptions DefaultRetryOptions = new();

    /// <summary>
    /// Adds VespaClient to the service collection with IHttpClientFactory integration and default resilience strategy
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="options">VespaClient configuration options</param>
    /// <param name="configureHttpClient">Optional additional HttpClient configuration</param>
    /// <returns>An IHttpClientBuilder for further configuration</returns>
    public static IHttpClientBuilder AddVespaClient(
        this IServiceCollection services,
        VespaClientOptions options,
        Action<HttpClient>? configureHttpClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        // Register options as singleton
        services.AddSingleton(options);

        // Configure HttpClient with factory
        var builder = services.AddHttpClient<IVespaClient, VespaClient>(
                httpClient => ConfigureHttpClient(httpClient, options, configureHttpClient))
            .ConfigurePrimaryHttpMessageHandler(() => BuildSocketsHttpHandler(options))
            .AddHttpMessageHandler(() => new VespaMetricsHandler());

        // Add default resilience strategy based on options
        ConfigureDefaultResilience(builder, options);

        return builder;
    }

    /// <summary>
    /// Adds named VespaClient to the service collection with IHttpClientFactory integration
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="name">The logical name of the client to configure</param>
    /// <param name="options">VespaClient configuration options</param>
    /// <param name="configureHttpClient">Optional additional HttpClient configuration</param>
    /// <returns>An IHttpClientBuilder for further configuration</returns>
    public static IHttpClientBuilder AddVespaClient(
        this IServiceCollection services,
        string name,
        VespaClientOptions options,
        Action<HttpClient>? configureHttpClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();

        // Register options with name
        services.AddKeyedSingleton(name, options);

        // Configure HttpClient with factory
        var builder = services.AddHttpClient(name,
                httpClient => ConfigureHttpClient(httpClient, options, configureHttpClient))
            .ConfigurePrimaryHttpMessageHandler(() => BuildSocketsHttpHandler(options))
            .AddHttpMessageHandler(() => new VespaMetricsHandler());

        // Add default resilience strategy based on options
        ConfigureDefaultResilience(builder, options);

        // Register factory for named VespaClient
        services.AddKeyedTransient<IVespaClient>(name, (sp, key) =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(name);
            var namedOptions = sp.GetRequiredKeyedService<VespaClientOptions>(name);
            var logger = sp.GetService<ILogger<VespaClient>>();
            return new VespaClient(httpClient, namedOptions, logger);
        });

        return builder;
    }

    /// <summary>
    /// Adds VespaClient with a custom resilience strategy
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="options">VespaClient configuration options</param>
    /// <param name="configureResilience">Configure custom resilience pipeline</param>
    /// <param name="configureHttpClient">Optional additional HttpClient configuration</param>
    /// <returns>An IHttpClientBuilder for further configuration</returns>
    public static IHttpClientBuilder AddVespaClientWithResilienceStrategy(
        this IServiceCollection services,
        VespaClientOptions options,
        Action<ResiliencePipelineBuilder<HttpResponseMessage>, ResilienceHandlerContext> configureResilience,
        Action<HttpClient>? configureHttpClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configureResilience);

        options.Validate();

        // Register options as singleton
        services.AddSingleton(options);

        // Configure HttpClient with factory
        var builder = services.AddHttpClient<IVespaClient, VespaClient>(
                httpClient => ConfigureHttpClient(httpClient, options, configureHttpClient))
            .ConfigurePrimaryHttpMessageHandler(() => BuildSocketsHttpHandler(options))
            .AddHttpMessageHandler(() => new VespaMetricsHandler());

        // Apply custom resilience strategy
        builder.AddResilienceHandler("vespa-custom-resilience", configureResilience);

        return builder;
    }

    private static void ConfigureHttpClient(
        HttpClient httpClient,
        VespaClientOptions options,
        Action<HttpClient>? configureHttpClient)
    {
        ConfigureHttpClientDefaults(httpClient, options.Endpoint, options);
        configureHttpClient?.Invoke(httpClient);
    }

    internal static void ConfigureHttpClientDefaults(
        HttpClient httpClient,
        string endpoint,
        VespaClientOptions options)
    {
        httpClient.BaseAddress = new Uri(endpoint);
        httpClient.Timeout = options.Timeout;
        httpClient.DefaultRequestVersion = new Version(2, 0);
        // Prefer HTTP/2, but fall back to HTTP/1.1 if the server doesn't negotiate it.
        // Over HTTPS, ALPN negotiates HTTP/2 automatically when supported.
        // Over plain HTTP (dev/Testcontainers), Vespa does not enable h2c by default,
        // so falling back keeps local and CI test runs working. Production Vespa Cloud
        // runs over HTTPS, so HTTP/2 is still the negotiated transport there.
        httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

        httpClient.DefaultRequestHeaders.Accept.Clear();
        httpClient.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        // Accept-Encoding is intentionally NOT set here. It is the SocketsHttpHandler's
        // AutomaticDecompression (configured in BuildSocketsHttpHandler when
        // options.UseCompression is true) that both adds the header AND transparently
        // decodes the response body. Setting the header manually on an HttpClient whose
        // handler doesn't decompress would return gzip bytes as an opaque string and
        // break JSON parsing — as happens when someone passes a bare HttpClient to the
        // direct VespaClient constructor.

        if (!string.IsNullOrEmpty(options.ApiKey))
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);

        if (options.DefaultRequestHeaders is not null)
            foreach (var (key, value) in options.DefaultRequestHeaders)
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
    }

    /// <summary>
    /// Configure default resilience strategy based on VespaClientOptions
    /// </summary>
    private static void ConfigureDefaultResilience(IHttpClientBuilder builder, VespaClientOptions options)
    {
        builder.AddResilienceHandler("vespa-default-resilience", (resilienceBuilder, context) =>
        {
            var logger = context.ServiceProvider.GetService<ILogger<VespaClient>>();

            // Add retry policy if enabled
            if (options is { EnableRetry: true, MaxRetryAttempts: > 0 })
            {
                resilienceBuilder.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = options.MaxRetryAttempts,
                    Delay = TimeSpan.FromMilliseconds(100),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = args =>
                    {
                        // Retry on transient failures + 429 Too Many Requests
                        if (args.Outcome.Result?.StatusCode == (HttpStatusCode)429)
                            return ValueTask.FromResult(true);
                        return DefaultRetryOptions.ShouldHandle(args);
                    },
                    OnRetry = args =>
                    {
                        VespaClientMetrics.RetryAttempts.Add(1);
                        if (logger != null)
                            LogRetryAttempt(logger, args.AttemptNumber, args.RetryDelay.TotalMilliseconds);
                        return ValueTask.CompletedTask;
                    }
                });
            }

            // Add circuit breaker if enabled
            if (options.EnableCircuitBreaker)
            {
                resilienceBuilder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    MinimumThroughput = options.CircuitBreakerFailureThreshold,
                    BreakDuration = options.CircuitBreakerDuration,
                    OnOpened = args =>
                    {
                        if (logger != null) LogCircuitBreakerOpened(logger);
                        return ValueTask.CompletedTask;
                    },
                    OnClosed = args =>
                    {
                        if (logger != null) LogCircuitBreakerClosed(logger);
                        return ValueTask.CompletedTask;
                    }
                });
            }
        });
    }

    /// <summary>
    /// Builds a <see cref="SocketsHttpHandler"/> configured for the given options,
    /// including mTLS client certificate when <see cref="VespaClientOptions.CreateClientCertificate"/> returns a value.
    /// </summary>
    /// <summary>
    /// Builds the <see cref="SocketsHttpHandler"/> configured from <paramref name="options"/>.
    /// Exposed for callers that construct <see cref="VespaClient"/> directly (e.g. tests
    /// and Testcontainers integrations) so they get the same connection pooling,
    /// compression, and mTLS behaviour as the DI path.
    /// </summary>
    public static SocketsHttpHandler BuildSocketsHttpHandler(VespaClientOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = options.MaxConnectionsPerServer,
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = options.UseCompression
                ? DecompressionMethods.GZip | DecompressionMethods.Deflate
                : DecompressionMethods.None,
            EnableMultipleHttp2Connections = true
        };

        ApplyConnectionPooling(handler, options);

        if (options.ConnectTimeout.HasValue)
            handler.ConnectTimeout = options.ConnectTimeout.Value;
        if (options.InitialHttp2StreamWindowSize.HasValue)
            handler.InitialHttp2StreamWindowSize = options.InitialHttp2StreamWindowSize.Value;

        var cert = options.CreateClientCertificate();
        if (cert is not null)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { cert }
            };
        }

        return handler;
    }

    private static void ApplyConnectionPooling(SocketsHttpHandler handler, VespaClientOptions options)
    {
        if (!options.UseConnectionPooling)
        {
            handler.PooledConnectionLifetime = TimeSpan.Zero;
            handler.PooledConnectionIdleTimeout = TimeSpan.Zero;
            handler.EnableMultipleHttp2Connections = false;
            return;
        }

        handler.PooledConnectionLifetime = options.PooledConnectionLifetime;
        handler.PooledConnectionIdleTimeout = options.PooledConnectionIdleTimeout;
    }

    [LoggerMessage(301, LogLevel.Warning, "Vespa retry #{Attempt} after {DelayMs:F0}ms")]
    private static partial void LogRetryAttempt(ILogger logger, int attempt, double delayMs);

    [LoggerMessage(302, LogLevel.Error, "Vespa circuit breaker opened")]
    private static partial void LogCircuitBreakerOpened(ILogger logger);

    [LoggerMessage(303, LogLevel.Information, "Vespa circuit breaker closed")]
    private static partial void LogCircuitBreakerClosed(ILogger logger);
}
