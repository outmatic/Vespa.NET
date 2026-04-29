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
    /// <summary>The Vespa Docker image used by default.</summary>
    public const string DefaultImage = "vespaengine/vespa:latest";

    /// <summary>The default HTTP port exposed by Vespa.</summary>
    public const int DefaultPort = 8080;

    /// <summary>The default config server port.</summary>
    public const int ConfigPort = 19071;

    private readonly string _image;
    private IContainer? _container;

    /// <summary>
    /// Initialises a new <see cref="VespaContainer"/> using the specified or default image.
    /// The Docker container is not created until <see cref="StartAsync"/> is called.
    /// </summary>
    /// <param name="image">Docker image to use (default: <see cref="DefaultImage"/>).</param>
    public VespaContainer(string image = DefaultImage) => _image = image;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    /// <summary>Starts the Vespa container and waits until it is healthy.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _container = new ContainerBuilder(_image)
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

        await _container.StartAsync(cancellationToken);
    }

    /// <summary>Stops the Vespa container.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
        => _container?.StopAsync(cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _container?.DisposeAsync() ?? ValueTask.CompletedTask;

    // ── Connection info ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the HTTP endpoint for the running Vespa instance
    /// (e.g. <c>http://localhost:32768</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown before <see cref="StartAsync"/> is called.</exception>
    public string Endpoint => _container is null
        ? throw new InvalidOperationException("Container has not been started. Call StartAsync() first.")
        : $"http://{_container.Hostname}:{_container.GetMappedPublicPort(DefaultPort)}";

    /// <summary>
    /// Returns the config server endpoint for deploying application packages
    /// (e.g. <c>http://localhost:32769</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown before <see cref="StartAsync"/> is called.</exception>
    public string ConfigEndpoint => _container is null
        ? throw new InvalidOperationException("Container has not been started. Call StartAsync() first.")
        : $"http://{_container.Hostname}:{_container.GetMappedPublicPort(ConfigPort)}";

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
        var options = new VespaClientOptions
        {
            Endpoint = Endpoint,
            ConfigServerEndpoint = ConfigEndpoint,
            DefaultNamespace = defaultNamespace,
            EnableRetry = false   // tests should fail fast
        };

        // Use the same SocketsHttpHandler that production (DI) code uses, so that
        // AutomaticDecompression, connection pooling, and HTTP/2 settings match.
        // Without this, a bare HttpClient has no AutomaticDecompression and would
        // receive gzipped bodies as opaque bytes.
        var handler = VespaServiceCollectionExtensions.BuildSocketsHttpHandler(options);
        var http = new HttpClient(handler, disposeHandler: true) { BaseAddress = new Uri(Endpoint) };
        var client = new VespaClient(http, options, logger);
        return (client, http);
    }

    /// <summary>
    /// Convenience overload — creates a <see cref="VespaClient"/> with a shared internal
    /// <see cref="HttpClient"/>. The HTTP client is not exposed; use
    /// <see cref="CreateClientWithHttp"/> when you need to dispose it explicitly.
    /// </summary>
    public VespaClient CreateClient(
        string defaultNamespace = "default",
        ILogger<VespaClient>? logger = null)
        => CreateClientWithHttp(defaultNamespace, logger).Client;

    /// <summary>
    /// Deploys a Vespa application package (ZIP file) to this container via the config server.
    /// </summary>
    /// <param name="packageZipPath">Path to the application package ZIP file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when deployment fails.</exception>
    public async Task DeployApplicationAsync(
        string packageZipPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageZipPath);

        using var http = new HttpClient();
        using var stream = File.OpenRead(packageZipPath);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");

        var url = $"{ConfigEndpoint}/application/v2/tenant/default/prepareandactivate";
        var response = await http.PostAsync(url, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Vespa application deployment failed ({response.StatusCode}): {body}");
        }
    }
}
