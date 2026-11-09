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

    private static readonly object DefaultRegistrationKey = new();

    /// <summary>
    /// Adds VespaClient to the service collection with IHttpClientFactory integration and default resilience strategy
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="options">VespaClient configuration options</param>
    /// <param name="configureHttpClient">Optional additional HttpClient configuration</param>
    /// <returns>An IHttpClientBuilder for further configuration</returns>
    /// <remarks>
    /// Registers a single unnamed <see cref="IVespaClient"/>. Repeated calls replace
    /// the previous options, HttpClient callback and resilience strategy; the last call wins.
    /// To talk to multiple Vespa endpoints, use the named overload
    /// (<see cref="AddVespaClient(IServiceCollection, string, VespaClientOptions, Action{HttpClient})"/>)
    /// and resolve clients with <c>GetRequiredKeyedService&lt;IVespaClient&gt;(name)</c>.
    /// <para>The <paramref name="options"/> instance is captured directly by the client
    /// factory; it is additionally exposed as a singleton for diagnostics without replacing
    /// consumer-registered <see cref="VespaClientOptions"/>, which never affect the client.</para>
    /// </remarks>
    public static IHttpClientBuilder AddVespaClient(
        this IServiceCollection services,
        VespaClientOptions options,
        Action<HttpClient>? configureHttpClient = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();
        return AddVespaClientCore(services, null, options, configureHttpClient, null);
    }

    private static Func<HttpClient, IServiceProvider, IVespaClient> CreatePreconfiguredVespaClient(
        VespaClientOptions options) =>
        (httpClient, sp) => new VespaClient(
            httpClient,
            options,
            sp.GetService<ILogger<VespaClient>>(),
            httpClientPreconfigured: true);

    /// <summary>
    /// Adds named VespaClient to the service collection with IHttpClientFactory integration
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="name">The logical name of the client to configure</param>
    /// <param name="options">VespaClient configuration options</param>
    /// <param name="configureHttpClient">Optional additional HttpClient configuration</param>
    /// <returns>An IHttpClientBuilder for further configuration</returns>
    /// <remarks>
    /// Repeated calls with the same name replace the previous Vespa configuration.
    /// Registrations with other names and the unnamed client are unaffected.
    /// </remarks>
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

        return AddVespaClientCore(services, name, options, configureHttpClient, null);
    }

    /// <summary>
    /// Adds VespaClient with a custom resilience strategy
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="options">VespaClient configuration options</param>
    /// <param name="configureResilience">Configure custom resilience pipeline</param>
    /// <param name="configureHttpClient">Optional additional HttpClient configuration</param>
    /// <returns>An IHttpClientBuilder for further configuration</returns>
    /// <remarks>
    /// Replaces any previous unnamed Vespa registration, including its options,
    /// HttpClient callback and resilience strategy.
    /// </remarks>
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
        return AddVespaClientCore(services, null, options, configureHttpClient, configureResilience);
    }

    private static IHttpClientBuilder AddVespaClientCore(
        IServiceCollection services,
        string? name,
        VespaClientOptions options,
        Action<HttpClient>? configureHttpClient,
        Action<ResiliencePipelineBuilder<HttpResponseMessage>, ResilienceHandlerContext>? configureResilience)
    {
        var registrationKey = name is null ? DefaultRegistrationKey : (object)name;
        var previous = services.FirstOrDefault(d => d.IsKeyedService &&
            d.ServiceType == typeof(VespaClientRegistration) && Equals(d.ServiceKey, registrationKey))
            ?.KeyedImplementationInstance as VespaClientRegistration;

        // Replace only our diagnostics options; consumer-registered options remain untouched.
        if (previous?.OptionsDescriptor is { } previousOptions)
            services.Remove(previousOptions);

        ServiceDescriptor? optionsDescriptor = null;
        if (name is not null || !services.Any(d => !d.IsKeyedService && d.ServiceType == typeof(VespaClientOptions)))
        {
            optionsDescriptor = name is null
                ? ServiceDescriptor.Singleton(options)
                : ServiceDescriptor.KeyedSingleton(name, options);
            services.Add(optionsDescriptor);
        }

        services.RemoveAllKeyed<VespaClientRegistration>(registrationKey);
        services.AddKeyedSingleton(registrationKey,
            new VespaClientRegistration(options, configureHttpClient, configureResilience, optionsDescriptor));

        var builder = services.AddHttpClient(name ?? DefaultHttpClientName);
        if (previous is null)
        {
            // Register the HTTP actions and pipeline once. Resolve an immutable registration
            // from DI so replacement does not stack handlers or mutate existing providers.
            builder.ConfigureHttpClient((sp, httpClient) =>
                {
                    var registration = sp.GetRequiredKeyedService<VespaClientRegistration>(registrationKey);
                    ConfigureHttpClient(httpClient, registration.Options, registration.ConfigureHttpClient);
                })
                .ConfigurePrimaryHttpMessageHandler(sp =>
                    BuildSocketsHttpHandler(sp.GetRequiredKeyedService<VespaClientRegistration>(registrationKey).Options))
                .AddHttpMessageHandler(() => new VespaMetricsHandler())
                .AddResilienceHandler("vespa-resilience", (pipeline, context) =>
                {
                    var registration = context.ServiceProvider.GetRequiredKeyedService<VespaClientRegistration>(registrationKey);
                    if (registration.ConfigureResilience is { } configure)
                        configure(pipeline, context);
                    else
                        ConfigureDefaultResilience(pipeline, context, registration.Options);
                });
        }

        if (name is null)
        {
            services.RemoveAll<IVespaClient>();
            builder.AddTypedClient(CreatePreconfiguredVespaClient(options));
        }
        else
        {
            services.RemoveAllKeyed<IVespaClient>(name);
            var createClient = CreatePreconfiguredVespaClient(options);
            services.AddKeyedTransient<IVespaClient>(name, (sp, _) =>
                createClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(name), sp));
        }

        return builder;
    }

    private sealed record VespaClientRegistration(
        VespaClientOptions Options,
        Action<HttpClient>? ConfigureHttpClient,
        Action<ResiliencePipelineBuilder<HttpResponseMessage>, ResilienceHandlerContext>? ConfigureResilience,
        ServiceDescriptor? OptionsDescriptor);

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
    private static void ConfigureDefaultResilience(
        ResiliencePipelineBuilder<HttpResponseMessage> resilienceBuilder,
        ResilienceHandlerContext context,
        VespaClientOptions options)
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
