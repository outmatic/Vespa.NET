using Vespa;
using Vespa.Models.Attributes;
using Vespa.Models.Schema;
using Vespa.Models.Tensors;
using Vespa.Testcontainers;
using Xunit;

namespace Vespa.IntegrationTests.Fixtures;

/// <summary>
/// Shared xUnit collection fixture that starts a single Vespa container, deploys the
/// application package, and exposes a ready-to-use <see cref="VespaClient"/>.
/// Enable with: VESPA_INTEGRATION_TESTS=1
/// </summary>
public sealed class VespaFixture : IAsyncLifetime
{
    public static bool IntegrationEnabled =>
        Environment.GetEnvironmentVariable("VESPA_INTEGRATION_TESTS") == "1";

    private VespaContainer? _vespa;
    private HttpClient? _http;

    public VespaClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!IntegrationEnabled) return;

        var explicitEndpoint = Environment.GetEnvironmentVariable("VESPA_ENDPOINT");

        if (explicitEndpoint is not null)
        {
            var configEndpoint = Environment.GetEnvironmentVariable("VESPA_CONFIG_ENDPOINT")
                ?? new UriBuilder(explicitEndpoint) { Port = 19071 }.Uri.ToString();

            var options = new VespaClientOptions
            {
                Endpoint = explicitEndpoint,
                ConfigServerEndpoint = configEndpoint,
                DefaultNamespace = "test",
                EnableRetry = false
            };
            var handler = VespaServiceCollectionExtensions.BuildSocketsHttpHandler(options);
            _http = new HttpClient(handler, disposeHandler: true) { BaseAddress = new Uri(explicitEndpoint) };
            Client = new VespaClient(_http, options);
        }
        else
        {
            _vespa = new VespaContainer();
            await _vespa.StartAsync();
            (Client, _http) = _vespa.CreateClientWithHttp("test");
        }

        await Client.Admin.DeploySchemaAsync<TestProduct>();
        await WaitForReadyAsync();
    }

    public async Task DisposeAsync()
    {
        _http?.Dispose();
        if (_vespa is not null)
            await _vespa.DisposeAsync();
    }

    /// <summary>The Vespa endpoint URL (for container-level assertions).</summary>
    public string? Endpoint => _vespa?.Endpoint;

    private async Task WaitForReadyAsync()
    {
        // Cold-start of a fresh vespa:latest image + schema deploy + cluster convergence
        // routinely needs 30-60s on a warm host, more on the first run.
        // Override via VESPA_READY_TIMEOUT_SECONDS when needed.
        var maxWaitSeconds = int.TryParse(
            Environment.GetEnvironmentVariable("VESPA_READY_TIMEOUT_SECONDS"),
            out var parsed) && parsed > 0 ? parsed : 300;

        var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await Client.IsReadyAsync())
                    return;
            }
            catch { /* not ready yet */ }

            await Task.Delay(2000);
        }

        throw new TimeoutException(
            $"Vespa did not become ready within {maxWaitSeconds}s. " +
            "Set VESPA_READY_TIMEOUT_SECONDS=<n> to override.");
    }
}

[CollectionDefinition("Vespa")]
public class VespaCollection : ICollectionFixture<VespaFixture> { }

// ── Shared test model ─────────────────────────────────────────────────────────

[VespaDocument("product", Namespace = "test")]
[VespaRankProfile("closeness_profile", Inherits = "default",
    FirstPhase = "closeness(field, embedding)")]
public sealed record TestProduct
{
    [VespaField(Name = "product_name", IndexingMode = IndexingMode.SummaryIndex)]
    public string Name { get; init; } = "";

    [VespaField(Name = "price", IndexingMode = IndexingMode.AttributeSummary)]
    public double Price { get; init; }

    [VespaField(Name = "category", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Category { get; init; } = "";

    [VespaField(Name = "description", IndexingMode = IndexingMode.SummaryIndex)]
    public string Description { get; init; } = "";

    [VespaField(Name = "in_stock", IndexingMode = IndexingMode.AttributeSummary)]
    public bool InStock { get; init; } = true;

    [VespaField(Name = "quantity", IndexingMode = IndexingMode.AttributeSummary)]
    public int Quantity { get; init; }

    [VespaField(Name = "tag", IndexingMode = IndexingMode.AttributeSummary)]
    public string Tag { get; init; } = "";

    [VespaField(Name = "embedding", IndexingMode = IndexingMode.AttributeSummary)]
    [VespaTensor("tensor<float>(x[4])", EnableIndex = true, DistanceMetric = DistanceMetric.Euclidean)]
    public VespaTensor? Embedding { get; init; }
}
