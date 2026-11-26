# Configuration Reference

Complete reference for `VespaClientOptions` and related configuration.

---

## VespaClientOptions

```csharp
var options = new VespaClientOptions
{
    // Required
    Endpoint              = "https://myapp.vespa-cloud.com",

    // Admin / Schema deployment
    ConfigServerEndpoint  = "http://localhost:19071",

    // Defaults
    DefaultNamespace      = "default",
    Tenant                = "default",         // config-server tenant (overridable per-call)

    // Authentication
    ApiKey                = Environment.GetEnvironmentVariable("VESPA_API_KEY"),

    // mTLS
    CertificatePath       = "/path/to/cert.pem",
    ClientKeyPath         = "/path/to/key.pem",
    CertificatePassword   = null,              // for PFX files
    ClientCertificate     = null,              // pre-loaded X509Certificate2

    // Timeouts & connections
    Timeout               = TimeSpan.FromSeconds(30),
    ConnectTimeout        = TimeSpan.FromSeconds(5),
    MaxConnectionsPerServer = 100,

    // Resilience
    EnableRetry           = true,
    MaxRetryAttempts      = 3,
    EnableCircuitBreaker  = true,
    CircuitBreakerDuration = TimeSpan.FromSeconds(30),
    CircuitBreakerMinimumThroughput = 10,

    // Performance
    UseConnectionPooling  = true,
    UseCompression        = true,
    UseProxy              = false,             // opt in to the system/environment proxy
    PooledConnectionLifetime   = TimeSpan.FromMinutes(10),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    InitialHttp2StreamWindowSize = null,

    // Observability
    RecordQueryTextInTraces = false,           // opt in to YQL/document-id tags on OTel spans

    // Custom headers
    DefaultRequestHeaders = new Dictionary<string, string>
    {
        ["X-Custom-Header"] = "value"
    }
};
```

Notes:
- `ApiKey`, `DefaultRequestHeaders`, timeout, compression, and mTLS settings apply to both the main query/document client and the admin config-server client.
- `Endpoint` and `ConfigServerEndpoint` must be absolute `http://`/`https://` URLs; `DefaultNamespace` and `Tenant` must be non-empty.
- `EnableRetry`/`EnableCircuitBreaker` install the HTTP resilience pipeline **only via the DI registrations** (`AddVespaClient`). A directly constructed `VespaClient` applies feed-level retry only and logs a warning. The options-only constructor `new VespaClient(options)` builds its own handler, so mTLS/compression/pooling work without DI; passing your own `HttpClient` together with certificate options throws.
- `CircuitBreakerMinimumThroughput` sets how many HTTP attempts the default DI circuit breaker must sample before it can open. It defaults to `5` and must be at least `2` when the breaker is enabled. Once the minimum is reached, a handled failure opens the circuit if at least half the sampled attempts have failed. Retries count as separate attempts. [Polly circuit breaker](https://www.pollydocs.org/strategies/circuit-breaker.html)
- `UseProxy` defaults to `false` (requests bypass the system proxy); set it to `true` to honor `HTTPS_PROXY` and friends.
- `RecordQueryTextInTraces` defaults to `false`: full YQL text and document ids are omitted from OpenTelemetry span tags unless you opt in.
- `ConnectTimeout` controls connection establishment time, not pooled-connection cleanup.
- `UseConnectionPooling = false` disables connection reuse by expiring pooled connections immediately.

---

## Generated JSON metadata

Register the document and projection types you use with reads, writes, visits, and search, including grouping summaries. The client handles the Vespa envelope and uses your `JsonSerializerContext` for the fields inside it.

```csharp
var options = new VespaClientOptions
{
    Endpoint = "http://localhost:8080",
    JsonModels = new VespaJsonModels()
        .With(ProductJsonContext.Default.Product, static (product, id) => product.WithId(id))
};

public sealed record Product([property: JsonPropertyName("product_name")] string Name)
{
    [JsonIgnore]
    public string? Id { get; private init; }

    public Product WithId(string id) => this with { Id = id };
}

[JsonSerializable(typeof(Product))]
internal sealed partial class ProductJsonContext : JsonSerializerContext;
```

Use the JSON attributes and converters in your context to match the schema. The optional function receives the short Vespa ID on reads. When you use `JsonModels`, register each fields type before sending a request, including projections. For custom query parameters or field update values, pass a `JsonElement` made with `JsonSerializer.SerializeToElement(value, typeInfo)`.

To read grouping outputs with generated metadata, pass your context's `Options` to `GetAggregation<T>` or `TryGetAggregation<T>`. Include each result type in that context.

Typed document selectors inspect the model's inheritance chain to resolve overrides and reject hidden properties. If your library forwards a generic model to these selectors, preserve the model metadata by applying `DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)` to the forwarding type parameter.

### Trimming and Native AOT

The paths that use generated metadata were verified in self-contained .NET 8 and .NET 10 applications with JSON reflection disabled. The verification covers macOS Arm64 and Linux x64, with Linux x64 executed under emulation. It includes document operations, search, visits, grouping, feed, dependency injection, and the registered schema and package APIs.

Use `VespaSchemaMetadata` for code-first packages and `WithFieldMaps()` when the client is shared with VectorData or Agent Framework. Follow each adapter's guidance for record binding and custom session data. [Schema generation](schema.md#generate-a-package-in-a-trimmed-or-native-aot-app) · [VectorData](../src/Vespa.NET.VectorData/README.md#trimmed-and-native-aot-apps) · [Agent Framework](../src/Vespa.NET.AgentFramework/README.md#trimmed-and-native-aot-apps)

## DI Integration

```csharp
// Basic registration
builder.Services.AddVespaClient(new VespaClientOptions
{
    Endpoint             = "http://localhost:8080",
    ConfigServerEndpoint = "http://localhost:19071",
    DefaultNamespace     = "myapp",
    EnableRetry          = true,
    MaxRetryAttempts     = 3
});

// Named client (multi-cluster)
builder.Services.AddVespaClient("analytics", new VespaClientOptions
{
    Endpoint = "http://analytics-vespa:8080"
});

// Custom resilience strategy
builder.Services.AddVespaClientWithResilienceStrategy(
    options,
    (pipeline, context) =>
    {
        pipeline.AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 5 });
    });

// Inject IVespaClient in your service
public class ProductService(IVespaClient vespa)
{
    public Task<VespaDocument<Product>?> GetProduct(string id)
        => vespa.Documents.GetAsync<Product>(id);
}
```

Resolve a named client with `GetRequiredKeyedService<IVespaClient>(name)`. Names are case-sensitive. `Vespa.NET` is reserved for the unnamed client, so choose a different name for a keyed registration.

---

## Vespa Cloud (mTLS)

This configures data-plane access for document, feed and search operations.
`ApiKey` means a data-plane bearer token, not the Cloud control-plane Application Key.
Cloud deployment uses the CLI/control plane; `client.Admin` targets a self-hosted
config server. See [Cloud access and deployment](agent-framework.md#vespa-cloud-access-and-deployment).

```csharp
var options = new VespaClientOptions
{
    Endpoint = "https://your-application-endpoint.vespa-app.cloud",
    CertificatePath = "/path/to/data-plane-public-cert.pem",
    ClientKeyPath = "/path/to/data-plane-private-key.pem"
};
using var client = new VespaClient(options);
```

Use the endpoint of your deployed application. For PFX/P12, set `CertificatePath`
and `CertificatePassword`, leaving `ClientKeyPath` unset. For a certificate loaded
by the application, set `ClientCertificate`. Token-enabled endpoints instead use
`ApiKey` with an authorized data-plane token and no client certificate. Set
`Endpoint` to the application's endpoint marked **Token** in Vespa Cloud; adding a
bearer header to its mTLS endpoint is not sufficient. Configure token permissions
in the application's `services.xml` clients before connecting. See the official
[token endpoint guide](https://docs.vespa.ai/en/security/guide.html#using-tokens).

---

## Health Checks

```csharp
builder.Services.AddHealthChecks()
    .AddVespa();  // registers VespaHealthCheck (calls IsReadyAsync)

// Or with custom name and tags
builder.Services.AddHealthChecks()
    .AddVespa(name: "vespa-cluster", tags: ["ready"]);
```

State/health helpers keep their best-effort return shapes (`false` / `null`) for normal failures, but they propagate `OperationCanceledException` when the supplied `CancellationToken` is cancelled.

---

## Observability

### OpenTelemetry Traces

```csharp
builder.Services.AddOpenTelemetry().WithTracing(b => b
    .AddSource(Vespa.NETActivitySource.Name)   // "Vespa.NET"
    .AddConsoleExporter());
```

**Spans:** `vespa.search`, `vespa.search.stream`, `vespa.search.group`, `vespa.search.nearest_neighbor`, `vespa.document.get`, `vespa.document.put`, `vespa.document.delete`, `vespa.document.update`, `vespa.feed.pipeline`

**Tags:** `vespa.yql`, `vespa.document_type`, `vespa.namespace`, `vespa.document_id`, `vespa.hits`, `vespa.top_k`, `vespa.feed.count`, `vespa.feed.success_count`

### Metrics

```csharp
builder.Services.AddOpenTelemetry().WithMetrics(m => m
    .AddMeter(Vespa.NETMetrics.MeterName));  // "Vespa.NET"
```

| Metric | Type | Description |
|---|---|---|
| `vespa.client.requests` | Counter | Total HTTP requests sent |
| `vespa.client.request_errors` | Counter | Failed HTTP requests |
| `vespa.client.request_duration` | Histogram | Request duration (ms) |
| `vespa.client.documents_written` | Counter | Documents written |
| `vespa.client.documents_deleted` | Counter | Documents deleted |
| `vespa.client.search_requests` | Counter | Search requests |
| `vespa.client.retry_attempts` | Counter | Retry attempts |

---

## Typed Exceptions

| Exception | When |
|---|---|
| `VespaNotFoundException` | Document not found (HTTP 404) |
| `VespaConditionNotMetException` | Conditional write failed (HTTP 412) |
| `VespaTimeoutException` | Request timed out (HTTP 408/504) |
| `VespaTooManyRequestsException` | Backpressure/throttling (HTTP 429) — back off and retry |
| `VespaServerException` | Server error (HTTP 5xx) |
| `VespaException` | All other Vespa errors |

```csharp
try
{
    // Vespa.NET 4.0: native conditions use rawCondition on model-aware writes.
    await client.Documents.PutAsync(product.Id, product, rawCondition: "product.version == 1");
}
catch (VespaConditionNotMetException)
{
    // Optimistic concurrency conflict
}
catch (VespaServerException ex)
{
    Console.WriteLine($"Server error {ex.StatusCode}: {ex.Message}");
}
```

---

## Testing with Testcontainers

```csharp
using Vespa.Testcontainers;

await using var vespa = new VespaContainer();
await vespa.StartAsync();

var (client, http) = vespa.CreateClientWithHttp("mynamespace");
using (http)
using (client)
{
    await client.Admin.DeploySchemaAsync<Product>();

    while (!await client.IsReadyAsync())
        await Task.Delay(2000);

    // Run integration tests ...
}
```

`CreateClientWithHttp()` returns two objects you own. Dispose the `VespaClient` to close its admin connection, then dispose the `HttpClient` to close the data connection. If you only need a Vespa client, use `CreateClient()` and let the container dispose both.

Calling `StartAsync` again while the container is running keeps the current deployment. After `StopAsync`, call `StartAsync` to restart the same container with its data. Read the endpoints again and create a new client after restart because Docker may assign different host ports. Use `DeployApplicationAsync` to deploy a ZIP and wait for its health endpoint within a separate readiness deadline.

**Environment variables:**
- `VESPA_INTEGRATION_TESTS=1` — enable the repository's live tests
- `VESPA_INTEGRATION_IMAGE` — select the Docker image for the core integration suite
- `VESPA_EXPECTED_VERSION` — require the shared fixture to report this server version
- `VESPA_ENDPOINT=http://localhost:8080` — use pre-running Vespa instead of container
