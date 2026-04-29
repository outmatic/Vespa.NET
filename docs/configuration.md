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
    CircuitBreakerFailureThreshold = 10,

    // Performance
    UseConnectionPooling  = true,
    UseCompression        = true,
    PooledConnectionLifetime   = TimeSpan.FromMinutes(10),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    InitialHttp2StreamWindowSize = null,

    // Custom headers
    DefaultRequestHeaders = new Dictionary<string, string>
    {
        ["X-Custom-Header"] = "value"
    }
};
```

Notes:
- `ApiKey`, `DefaultRequestHeaders`, timeout, compression, and mTLS settings apply to both the main query/document client and the admin config-server client.
- `ConnectTimeout` controls connection establishment time, not pooled-connection cleanup.
- `UseConnectionPooling = false` disables connection reuse by expiring pooled connections immediately.

---

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

---

## Vespa Cloud (mTLS)

```csharp
var options = new VespaClientOptions
{
    Endpoint = "https://myapp.vespa-cloud.com",

    // Option A: PEM certificate + private key
    CertificatePath = "/path/to/cert.pem",
    ClientKeyPath   = "/path/to/key.pem",

    // Option B: PFX file
    CertificatePath     = "/path/to/cert.pfx",
    CertificatePassword = "passphrase",

    // Option C: pre-loaded X509Certificate2
    ClientCertificate = myCert,

    // Bearer token auth (alternative to mTLS)
    ApiKey = Environment.GetEnvironmentVariable("VESPA_API_KEY")
};
```

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
| `VespaTimeoutException` | Request timed out (HTTP 504) |
| `VespaServerException` | Server error (HTTP 5xx) |
| `VespaException` | All other Vespa errors |

```csharp
try
{
    await client.Documents.PutAsync(product.Id, product, condition: "product.version == 1");
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
{
    await client.Admin.DeploySchemaAsync<Product>();

    while (!await client.IsReadyAsync())
        await Task.Delay(2000);

    // Run integration tests ...
}
```

**Environment variables:**
- `VESPA_INTEGRATION_TESTS=1` — enable integration tests in CI
- `VESPA_ENDPOINT=http://localhost:8080` — use pre-running Vespa instead of container
