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
    UseCompression        = true,              // decompress gzip and deflate responses
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
- The SDK applies `ApiKey`, `DefaultRequestHeaders` and timeout to both the data-plane client and the admin client. When it builds the handlers, it also applies the connection and certificate options to both.
- `Endpoint` and `ConfigServerEndpoint` must be absolute `http://`/`https://` URLs; `DefaultNamespace` and `Tenant` must be non-empty.
- `EnableRetry` and `EnableCircuitBreaker` configure HTTP resilience through `AddVespaClient`. A directly constructed client uses feed-level retry when enabled and logs a warning about HTTP resilience if a logger is supplied.
- `UseCompression` enables automatic gzip and deflate response decompression on SDK-built handlers. The handler sets `Accept-Encoding`. Request bodies are sent uncompressed. For a caller-supplied `HttpClient`, configure response decompression on its handler. [Automatic decompression](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.automaticdecompression)
- `CircuitBreakerMinimumThroughput` sets how many HTTP attempts the default DI circuit breaker must sample before it can open. It defaults to `5` and must be at least `2` when the breaker is enabled. Once the minimum is reached, a handled failure opens the circuit if at least half the sampled attempts have failed. Retries count as separate attempts. [Polly circuit breaker](https://www.pollydocs.org/strategies/circuit-breaker.html)
- `UseProxy` defaults to `false` (requests bypass the system proxy); set it to `true` to honor `HTTPS_PROXY` and friends.
- `RecordQueryTextInTraces` defaults to `false`: full YQL text and document ids are omitted from OpenTelemetry span tags unless you opt in.
- `ConnectTimeout` controls connection establishment time, not pooled-connection cleanup.
- `UseConnectionPooling = false` disables connection reuse by expiring pooled connections immediately.

---

## Generated JSON metadata

Register the document and projection types you use with reads, writes, visits, and search, including grouping summaries. The client handles the Vespa envelope and uses your `JsonSerializerContext` for the fields inside it.

```csharp
using System.Text.Json.Serialization;
using Vespa;

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

Set `PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower` to match the client's default field names, and declare any custom field names with `[JsonPropertyName]` in the generated model.

To read grouping outputs with generated metadata, pass your context's `Options` to `GetAggregation<T>` or `TryGetAggregation<T>`. Include each result type in that context.

Typed document selectors inspect the model's inheritance chain to resolve overrides and reject hidden properties. If your library forwards a generic model to these selectors, preserve the model metadata by applying `DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)` to the forwarding type parameter.

### Trimming and Native AOT

Use `Vespa.NET` and `Vespa.NET.Models` 4.0 with registered JSON metadata in trimmed and Native AOT applications. We verified the installed packages in self-contained .NET 8 and .NET 10 applications on macOS Arm64 and Linux x64, with Linux x64 run under emulation. JSON reflection was disabled in all runs. The HTTP fixture covers document operations, search, visits, grouping, feed, dependency injection and package generation with registered schema types.

Compatibility applies to the paths that use registered metadata. Runtime discovery through `VespaSchemaBuilder` and direct JSON deserialization of `SearchHit<T>` still use reflection or runtime code generation. `Vespa.NET.Testcontainers` was part of the installed dependency graph, and its Docker helper is exercised in the JIT integration tests. [Search response deserialization](search.md#read-a-response-with-generated-json-metadata)

Use `VespaSchemaMetadata` for code-first packages and `WithFieldMaps()` when the client is shared with VectorData or Agent Framework. The adapter paths verified with the same fixture were VectorData record operations and search, and Agent Framework session, chat history and file persistence. Follow each adapter's guidance for record binding and custom session data. [Schema generation](schema.md#generate-a-package-in-a-trimmed-or-native-aot-app) · [VectorData](../src/Vespa.NET.VectorData/README.md#trimmed-and-native-aot-apps) · [Agent Framework](../src/Vespa.NET.AgentFramework/README.md#trimmed-and-native-aot-apps)

With JSON reflection disabled, chat-history snapshots can serialize `bool`, `int`, `long`, `float`, `double`, `decimal`, `string`, `Guid`, `DateTime`, and their one-dimensional arrays as tool results. For other result types, such as enums, dictionaries or custom objects, enable JSON reflection where supported or pass a `JsonElement` created with `JsonSerializer.SerializeToElement(value, typeInfo)`.

The warning inventory lists the fallback paths, the guards they rely on, and the changes that would remove each suppression. [Trim and AOT warning inventory](trimming.md)

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

Use the options-only constructor or `AddVespaClient` to configure credentials for the deployed Vespa data-plane endpoint. For Cloud application deployment, use the CLI or control plane. `client.Admin` targets a self-hosted config server. [Cloud access and deployment](agent-framework.md#vespa-cloud-access-and-deployment)

```csharp
var options = new VespaClientOptions
{
    Endpoint = "https://your-application-endpoint.vespa-app.cloud",
    CertificatePath = "/path/to/data-plane-public-cert.pem",
    ClientKeyPath = "/path/to/data-plane-private-key.pem"
};
using var client = new VespaClient(options);
```

For PFX/P12, set `CertificatePath` and `CertificatePassword` and leave `ClientKeyPath` unset. If your application already holds an `X509Certificate2`, pass it as `ClientCertificate` and keep it alive until all clients that use it are disposed.

With `new VespaClient(options)`, disposing the client closes its data-plane and admin connections and disposes certificates it loaded from disk. With `new VespaClient(httpClient, options)`, the caller owns the supplied `HttpClient` and its handler. Configure mTLS on that handler and leave `CertificatePath` and `ClientCertificate` unset in the options passed to the constructor. The admin client uses its own handler, so use the options-only or DI path when both endpoints need the certificate settings.

For token authentication, use the deployed endpoint marked **Token** and set `ApiKey` to an authorized data-plane bearer token. Configure that token's permissions in the application's `services.xml` clients. Cloud deployment uses separate credentials. [Token endpoint guide](https://docs.vespa.ai/en/security/guide.html#using-tokens)

---

## Health Checks

```csharp
builder.Services.AddHealthChecks()
    .AddVespa(name: "vespa", tags: ["ready"])
    .AddVespa(clientName: "analytics", name: "vespa-analytics", tags: ["ready"]);
```

The first check uses the unnamed client. The second resolves the keyed client registered with `AddVespaClient("analytics", options)`. Each check calls `IsReadyAsync`. Choose a unique health check name for each registration.

State/health helpers keep their best-effort return shapes (`false` / `null`) for normal failures, but they propagate `OperationCanceledException` when the supplied `CancellationToken` is cancelled.

---

## Observability

### OpenTelemetry Traces

Use `OpenTelemetry.Extensions.Hosting` for registration and `OpenTelemetry.Exporter.Console` for this console exporter.

```csharp
using OpenTelemetry.Trace;
using Vespa;

builder.Services.AddOpenTelemetry().WithTracing(b => b
    .AddSource(VespaActivitySource.Name)
    .AddConsoleExporter());
```

**Spans:** `vespa.search`, `vespa.search.stream`, `vespa.search.group`, `vespa.search.nearest_neighbor`, `vespa.document.get`, `vespa.document.put`, `vespa.document.delete`, `vespa.document.update`, `vespa.feed.pipeline`

Tags include `vespa.document_type`, `vespa.namespace`, `vespa.hits`, `vespa.top_k`, `vespa.feed.count` and `vespa.feed.success_count`. Set `RecordQueryTextInTraces = true` to include the `vespa.yql` and `vespa.document_id` tags.

### Metrics

```csharp
using OpenTelemetry.Metrics;
using Vespa;

builder.Services.AddOpenTelemetry().WithMetrics(m => m
    .AddMeter(VespaClientMetrics.MeterName));
```

The DI registration adds `VespaMetricsHandler` to the data-plane pipeline. When supplying your own HTTP client, add the handler to that pipeline to collect these request metrics.

| Metric | Type | Description |
|---|---|---|
| `vespa.client.requests` | Counter | HTTP calls through the metrics handler |
| `vespa.client.request_errors` | Counter | Non-success responses and transport failures, excluding caller cancellation |
| `vespa.client.request_duration` | Histogram | Time in the HTTP pipeline, in milliseconds |
| `vespa.client.documents_written` | Counter | POST or PUT document calls that received an HTTP response |
| `vespa.client.documents_deleted` | Counter | DELETE document calls that received an HTTP response |
| `vespa.client.search_requests` | Counter | Search calls that received an HTTP response |
| `vespa.client.retry_attempts` | Counter | HTTP retries from the default DI retry policy |

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
