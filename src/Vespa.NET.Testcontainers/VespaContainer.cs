using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;
using Vespa;

namespace Vespa.Testcontainers;

/// <summary>
/// A Testcontainers-based Vespa container for integration and end-to-end tests.
/// </summary>
/// <example>
/// <code>
/// await using var vespa = new VespaContainer();
/// await vespa.StartAsync();
///
/// var client = vespa.CreateClient();
/// var healthy = await client.HealthCheckAsync();
/// </code>
/// </example>
public sealed class VespaContainer : IAsyncDisposable
{
    /// <summary>
    /// The Vespa Docker image used by default. Pinned to the major version so test
    /// runs are reproducible and not broken by silent <c>latest</c> upgrades.
    /// </summary>
    public const string DefaultImage = "vespaengine/vespa:8";

    /// <summary>The default HTTP port exposed by Vespa.</summary>
    public const int DefaultPort = 8080;

    /// <summary>The default config server port.</summary>
    public const int ConfigPort = 19071;

    private readonly string _image;
    private readonly SemaphoreSlim _lifecycle = new(1);
    private readonly List<IDisposable> _ownedClients = [];
    private IContainer? _container;
    private volatile ContainerEndpoints? _endpoints;
    private volatile bool _disposed;

    private sealed record ContainerEndpoints(string Application, string Config);

    /// <summary>
    /// Initialises a new <see cref="VespaContainer"/> using the specified or default image.
    /// The Docker container is not created until <see cref="StartAsync"/> is called.
    /// </summary>
    /// <param name="image">Docker image to use (default: <see cref="DefaultImage"/>).</param>
    public VespaContainer(string image = DefaultImage) => _image = image;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    /// <summary>Starts the Vespa container and waits until it is healthy.</summary>
    /// <remarks>Repeated calls while started do nothing. After a stop, this restarts the same container and refreshes its endpoints.</remarks>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_endpoints is not null)
                return;

            _container ??= BuildContainer();
            await _container.StartAsync(cancellationToken);
            _endpoints = new ContainerEndpoints(
                $"http://{_container.Hostname}:{_container.GetMappedPublicPort(DefaultPort)}",
                $"http://{_container.Hostname}:{_container.GetMappedPublicPort(ConfigPort)}");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private IContainer BuildContainer() =>
        new ContainerBuilder(_image)
            .WithPortBinding(DefaultPort, assignRandomHostPort: true)
            .WithPortBinding(ConfigPort, assignRandomHostPort: true)
            .WithEnvironment("VESPA_IGNORE_NOT_ENOUGH_MEMORY", "true")
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r
                        .ForPort(ConfigPort)
                        .ForPath("/state/v1/health")
                        .ForStatusCode(System.Net.HttpStatusCode.OK)))
            .Build();

    /// <summary>Stops the Vespa container.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _endpoints = null;
            if (_container is not null)
                await _container.StopAsync(cancellationToken);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            try
            {
                lock (_ownedClients)
                {
                    _disposed = true;
                    _endpoints = null;
                    foreach (var client in _ownedClients)
                        client.Dispose();
                    _ownedClients.Clear();
                }
            }
            finally
            {
                if (_container is not null)
                {
                    await _container.DisposeAsync();
                    _container = null;
                }
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    // ── Connection info ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the HTTP endpoint for the running Vespa instance
    /// (e.g. <c>http://localhost:32768</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The container has not finished starting or has been stopped.</exception>
    public string Endpoint => GetEndpoints().Application;

    /// <summary>
    /// Returns the config server endpoint for deploying application packages
    /// (e.g. <c>http://localhost:32769</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The container has not finished starting or has been stopped.</exception>
    public string ConfigEndpoint => GetEndpoints().Config;

    private ContainerEndpoints GetEndpoints()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _endpoints ?? throw new InvalidOperationException("The container is not running. Call StartAsync() first.");
    }

    // ── Client factory ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a fully configured <see cref="VespaClient"/> connected to this container.
    /// </summary>
    /// <param name="defaultNamespace">Default document namespace (default: <c>"default"</c>).</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>
    /// A tuple of (<see cref="VespaClient"/>, <see cref="HttpClient"/>).
    /// Dispose the <see cref="HttpClient"/> when done to release connections.
    /// </returns>
    public (VespaClient Client, HttpClient Http) CreateClientWithHttp(
        string defaultNamespace = "default",
        ILogger<VespaClient>? logger = null)
    {
        var endpoints = GetEndpoints();
        var options = new VespaClientOptions
        {
            Endpoint = endpoints.Application,
            ConfigServerEndpoint = endpoints.Config,
            DefaultNamespace = defaultNamespace,
            EnableRetry = false   // tests should fail fast
        };

        // Use the same SocketsHttpHandler that production (DI) code uses, so that
        // AutomaticDecompression, connection pooling, and HTTP/2 settings match.
        // Without this, a bare HttpClient has no AutomaticDecompression and would
        // receive gzipped bodies as opaque bytes.
        var handler = VespaServiceCollectionExtensions.BuildSocketsHttpHandler(options);
        var http = new HttpClient(handler, disposeHandler: true) { BaseAddress = new Uri(endpoints.Application) };
        var client = new VespaClient(http, options, logger);
        return (client, http);
    }

    /// <summary>
    /// Convenience overload — creates a <see cref="VespaClient"/> whose
    /// <see cref="HttpClient"/> is owned by this container and disposed in
    /// <see cref="DisposeAsync"/>. Use <see cref="CreateClientWithHttp"/> when you
    /// need to control the HTTP client's lifetime yourself.
    /// </summary>
    public VespaClient CreateClient(
        string defaultNamespace = "default",
        ILogger<VespaClient>? logger = null)
    {
        lock (_ownedClients)
        {
            var (client, http) = CreateClientWithHttp(defaultNamespace, logger);
            _ownedClients.Add(client);
            _ownedClients.Add(http);
            return client;
        }
    }

    /// <summary>
    /// Deploys a Vespa application package (ZIP file) to this container via the config server.
    /// </summary>
    /// <param name="packageZipPath">Path to the application package ZIP file.</param>
    /// <param name="deployTimeout">
    /// Timeout for the deployment HTTP request itself. Default: 100 seconds
    /// (the <see cref="HttpClient"/> default).
    /// </param>
    /// <param name="readyTimeout">
    /// How long to wait for the application port to serve health after activation.
    /// Default: 2 minutes. Must be positive and at most 4,294,967,294 milliseconds.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Deployment failed or the application was not ready before the deadline.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The readiness timeout is outside the supported range.</exception>
    public async Task DeployApplicationAsync(
        string packageZipPath,
        TimeSpan? deployTimeout = null,
        TimeSpan? readyTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageZipPath);
        var readinessTimeout = readyTimeout ?? TimeSpan.FromMinutes(2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(readinessTimeout, TimeSpan.Zero, nameof(readyTimeout));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(readinessTimeout, TimeSpan.FromMilliseconds(uint.MaxValue - 1), nameof(readyTimeout));
        var endpoints = GetEndpoints();

        using var http = new HttpClient();
        if (deployTimeout is { } requestTimeout)
            http.Timeout = requestTimeout;
        using (var stream = File.OpenRead(packageZipPath))
        using (var content = new StreamContent(stream))
        {
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            var url = $"{endpoints.Config}/application/v2/tenant/default/prepareandactivate";
            using var response = await http.PostAsync(url, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"Vespa application deployment failed ({response.StatusCode}): {body}");
            }
        }

        // prepareandactivate returns before the application port serves traffic —
        // wait for the app health endpoint so callers don't race the activation.
        await WaitForApplicationReadyAsync(http, endpoints.Application, readinessTimeout, cancellationToken);
    }

    internal static async Task WaitForApplicationReadyAsync(
        HttpClient http, string endpoint, TimeSpan deadline, CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        var healthUrl = $"{endpoint}/state/v1/health";
        var pollInterval = TimeSpan.FromMilliseconds(500);
        var started = timeProvider.GetTimestamp();
        using var timeout = new CancellationTokenSource(deadline, timeProvider);
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        bool HasTimeLeft() => !probe.IsCancellationRequested && timeProvider.GetElapsedTime(started) < deadline;

        try
        {
            while (HasTimeLeft())
            {
                try
                {
                    using var health = await http.GetAsync(healthUrl, HttpCompletionOption.ResponseHeadersRead, probe.Token);
                    if (health.IsSuccessStatusCode && HasTimeLeft())
                        return;
                }
                catch (HttpRequestException)
                {
                    // The application port may not be serving yet.
                }
                catch (OperationCanceledException) when (!probe.IsCancellationRequested)
                {
                    // An individual HttpClient timeout can be retried within the readiness budget.
                }

                if (HasTimeLeft())
                    await Task.Delay(pollInterval, timeProvider, probe.Token);
            }
        }
        catch (OperationCanceledException) when (probe.IsCancellationRequested)
        {
            // Report whether the caller or the readiness deadline canceled the probe.
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            $"Vespa application did not become ready on {endpoint} within {deadline:c} after deployment.");
    }
}
