using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    /// <summary>Logical name of the default (unnamed) Vespa HttpClient registration.</summary>
    private const string DefaultHttpClientName = "Vespa.NET";

    /// <summary>
    /// Adds VespaClient to the service collection with IHttpClientFactory integration and default resilience strategy
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="options">VespaClient configuration options</param>
    /// <param name="configureHttpClient">Optional additional HttpClient configuration</param>
    /// <returns>An IHttpClientBuilder for further configuration</returns>
    /// <remarks>
    /// Call this overload once per container: it registers a single unnamed
    /// <see cref="IVespaClient"/> and throws <see cref="InvalidOperationException"/> on a
    /// second call (stacked registrations would silently combine both configurations).
    /// To talk to multiple Vespa endpoints, use the named overload
    /// (<see cref="AddVespaClient(IServiceCollection, string, VespaClientOptions, Action{HttpClient})"/>)
    /// and resolve clients with <c>GetRequiredKeyedService&lt;IVespaClient&gt;(name)</c>.
    /// <para>The <paramref name="options"/> instance is captured directly by the client
    /// factory; it is additionally exposed via <c>TryAddSingleton</c> for diagnostics, so a
    /// consumer-registered <see cref="VespaClientOptions"/> singleton never affects the client.</para>
    /// </remarks>
    public static IHttpClientBuilder AddVespaClient(
        this IServiceCollection services,
        VespaClientOptions options,
        Action<HttpClient>? configureHttpClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();
        ThrowIfUnnamedClientRegistered(services);

        // TryAdd: exposed for diagnostics only — the client factory captures `options`
        // in a closure, so a pre-existing consumer registration cannot collide with it.
        services.TryAddSingleton(options);

        // Configure HttpClient with factory. Registered as a NAMED client +
        // AddTypedClient: the generic AddHttpClient<IVespaClient, VespaClient>
        // would leave a second IVespaClient registration activated through the
        // public constructor, which re-applies defaults over the factory config.
        var builder = services.AddHttpClient(DefaultHttpClientName,
                httpClient => ConfigureHttpClient(httpClient, options, configureHttpClient))
            .ConfigurePrimaryHttpMessageHandler(() => BuildSocketsHttpHandler(options))
            .AddHttpMessageHandler(() => new VespaMetricsHandler())
            .AddTypedClient(CreatePreconfiguredVespaClient(options));

        // Add default resilience strategy based on options
        ConfigureDefaultResilience(builder, options);

        return builder;
    }

    private static Func<HttpClient, IServiceProvider, IVespaClient> CreatePreconfiguredVespaClient(
        VespaClientOptions options) =>
        (httpClient, sp) => new VespaClient(
            httpClient,
            options,
            sp.GetService<ILogger<VespaClient>>(),
            httpClientPreconfigured: true);

    private static void ThrowIfUnnamedClientRegistered(IServiceCollection services)
    {
        if (services.Any(d => !d.IsKeyedService && d.ServiceType == typeof(IVespaClient)))
            throw new InvalidOperationException(
                "An unnamed IVespaClient is already registered. Call AddVespaClient once per container, " +
                "or use the named overload AddVespaClient(name, options) to talk to multiple Vespa endpoints.");
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
            return new VespaClient(httpClient, namedOptions, logger, httpClientPreconfigured: true);
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
        ThrowIfUnnamedClientRegistered(services);

        // TryAdd: exposed for diagnostics only — see AddVespaClient
        services.TryAddSingleton(options);

        // Configure HttpClient with factory (named + AddTypedClient — see AddVespaClient)
        var builder = services.AddHttpClient(DefaultHttpClientName,
                httpClient => ConfigureHttpClient(httpClient, options, configureHttpClient))
            .ConfigurePrimaryHttpMessageHandler(() => BuildSocketsHttpHandler(options))
            .AddHttpMessageHandler(() => new VespaMetricsHandler())
            .AddTypedClient(CreatePreconfiguredVespaClient(options));

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

        // Validated Add (not TryAddWithoutValidation): a malformed header name/value
        // should fail loudly at configuration time, not corrupt requests silently.
        if (options.DefaultRequestHeaders is not null)
            foreach (var (key, value) in options.DefaultRequestHeaders)
                try
                {
                    httpClient.DefaultRequestHeaders.Add(key, value);
                }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException)
                {
                    throw new ArgumentException(
                        $"Invalid default request header '{key}': {ex.Message}",
                        nameof(options), ex);
                }
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
    /// Builds the <see cref="SocketsHttpHandler"/> configured from <paramref name="options"/>,
    /// including the mTLS client certificate when configured.
    /// Exposed for callers that construct <see cref="VespaClient"/> directly (e.g. tests
    /// and Testcontainers integrations) so they get the same connection pooling,
    /// compression, and mTLS behaviour as the DI path.
    /// </summary>
    public static SocketsHttpHandler BuildSocketsHttpHandler(VespaClientOptions options) =>
        BuildSocketsHttpHandler(options, out _);

    /// <summary>
    /// Same as <see cref="BuildSocketsHttpHandler(VespaClientOptions)"/>, additionally
    /// returning the certificate this call loaded from disk (PFX/PEM options) so the
    /// caller can dispose it with the handler. <paramref name="loadedCertificate"/> is
    /// <see langword="null"/> when mTLS is not configured or when
    /// <see cref="VespaClientOptions.ClientCertificate"/> supplied a caller-owned instance.
    /// </summary>
    internal static SocketsHttpHandler BuildSocketsHttpHandler(
        VespaClientOptions options,
        out X509Certificate2? loadedCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = options.MaxConnectionsPerServer,
            UseProxy = options.UseProxy,
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
        loadedCertificate = ReferenceEquals(cert, options.ClientCertificate) ? null : cert;
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
