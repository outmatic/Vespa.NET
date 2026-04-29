<div align="center">

# Vespa.NET

**The .NET SDK for [Vespa.ai](https://vespa.ai) — from schema to search in one line of code.**

[![NuGet](https://img.shields.io/nuget/v/Vespa.NET?style=flat-square&logo=nuget&color=004880)](https://www.nuget.org/packages/Vespa.NET)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512bd4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)
[![.NET 8](https://img.shields.io/badge/.NET-8.0_(LTS)-512bd4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-green?style=flat-square)](https://github.com/outmatic/Vespa.NET/blob/main/LICENSE)

</div>

---

```csharp
// Define your model → deploy schema → search. That's it.
await client.Admin.DeploySchemaAsync<Product>();
await client.Documents.PutAsync(product.Id, product);
var results = await client.Search.NearestNeighborSearchAsync<Product>(embedding, p => p.Embedding, topK: 10);
```

---

## Why Vespa.NET?

| Without Vespa.NET | With Vespa.NET |
|---|---|
| Write `.sd` schema files by hand | `[VespaDocument]` + `[VespaField]` on your C# model |
| Build ZIP packages, POST to config server | `await client.Admin.DeploySchemaAsync<Product>()` |
| Construct YQL strings with string concatenation | Fluent `YqlBuilder<T>` with lambda field selectors |
| Manual HTTP calls, JSON parsing, error handling | Typed `SearchAsync<T>`, `GetAsync<T>`, `BulkPutAsync<T>` |
| Roll your own retry, circuit breaker, metrics | Built-in Polly resilience + OpenTelemetry out of the box |

---

## Features

| | |
|---|---|
| **Schema** | Code-first `.sd` generation from attributes, multi-type deploy, [custom `services.xml`](https://github.com/outmatic/Vespa.NET/blob/main/docs/schema.md) |
| **Documents** | CRUD, conditional writes, field-level ops, visit/iterate, group/number addressing (read+write), selection-based update/delete/copy with continuation auto-loop + crash-safe manual paging |
| **Search** | Full-text, nearest-neighbor, hybrid, streaming, auto-paginated `IAsyncEnumerable` |
| **YQL** | Fluent type-safe builder with boolean composition, grouping, ranking DSL |
| **Feed** | Parallel `/document/v1` pipeline with `Channel<T>` backpressure, progress callbacks |
| **Multi-tenant** | [`[VespaExtraFields]`](https://github.com/outmatic/Vespa.NET/blob/main/docs/schema.md#dynamic-fields-with-vespaextrafields) catch-all for dynamic fields, configurable tenant |
| **Cloud** | mTLS (PEM, PFX, in-memory cert), Bearer token auth |
| **Ops** | OpenTelemetry traces + metrics, ASP.NET Core health checks, Testcontainers |
| **Performance** | HTTP/2 multiplexing, connection pooling, GZip/Deflate, `ResponseHeadersRead` |
| **Resilience** | Retry + circuit breaker via Polly, zero-allocation `[LoggerMessage]` logging |

---

## Quick Start

### 1. Install

```bash
dotnet add package Vespa.NET
```

### 2. Define a model

```csharp
[VespaDocument("product", Namespace = "myapp")]
public record Product
{
    [VespaId]
    public string Id { get; init; } = "";

    [VespaField(Name = "product_name", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Name { get; init; } = "";

    [VespaField(Name = "price", IndexingMode = IndexingMode.AttributeSummary)]
    public decimal Price { get; init; }

    [VespaTensor("tensor<float>(x[128])", EnableIndex = true, DistanceMetric = DistanceMetric.Euclidean)]
    public VespaTensor Embedding { get; init; } = null!;
}
```

### 3. Deploy, index, search

```csharp
var options = new VespaClientOptions
{
    Endpoint         = "http://localhost:8080",
    DefaultNamespace = "myapp"
};

using var httpClient = new HttpClient { BaseAddress = new Uri(options.Endpoint) };
using var client     = new VespaClient(httpClient, options);

// Deploy schema from C# attributes
await client.Admin.DeploySchemaAsync<Product>();

// Index a document
var product = new Product { Id = "p-1", Name = "Laptop", Price = 999.99m, Embedding = embeddings };
await client.Documents.PutAsync(product.Id, product);

// Search
var results = await client.Search.NearestNeighborSearchAsync<Product>(
    queryEmbedding, p => p.Embedding, topK: 10);
```

> **Tip:**
> With `[VespaDocument]` on your model, `documentType` and `namespace` are inferred automatically in all operations. No strings needed.

---

## Model-Aware API

When your model has `[VespaDocument]`, all operations infer types automatically. `[VespaField(Name)]` drives both schema generation and JSON serialization.

```csharp
// CRUD — no documentType needed
await client.Documents.PutAsync(product.Id, product);
var doc = await client.Documents.GetAsync<Product>(product.Id);
await client.Documents.DeleteAsync<Product>(product.Id);

// Field-level update with typed lambda builder
await client.Documents.UpdateFieldsAsync<Product>(product.Id, ops => ops
    .Field(p => p.Name, FieldOp.Assign("New Name"))
    .Field(p => p.Price, FieldOp.Multiply(0.9)));

// Nearest-neighbor — field name resolved via lambda
var results = await client.Search.NearestNeighborSearchAsync<Product>(
    queryEmbedding, p => p.Embedding, topK: 10);

// Visit
await foreach (var d in client.Documents.VisitAsync<Product>(selection: "product.price > 100"))
    Process(d.Fields);

// Selection-based bulk ops — auto-loops on Vespa's continuation token
var resp = await client.Documents.UpdateBySelectionAsync(
    "product.category == \"legacy\"",
    new() { ["status"] = FieldOp.Assign("archived") },
    "product", cluster: "content");
// resp.DocumentCount is the total across all chunks

// Cross-cluster copy
await client.Documents.CopyBySelectionAsync(
    "product.tier == \"cold\"", "product",
    cluster: "hot", destinationCluster: "cold");
```

> **Note:**
> **ID normalisation:** pass either `"p-1"` or `"id:myapp:product::p-1"` — the client strips the prefix automatically.

---

## YQL Builder

```csharp
using Vespa.Query;

var yql = YqlBuilder<Product>
    .Select(p => p.Name, p => p.Price)
    .Where(w => w.Field(p => p.Price).GreaterThan(10)
                 .And(sub => sub.Field(p => p.Name).Contains("laptop")))
    .OrderBy(p => p.Price, descending: true)
    .Limit(20)
    .Build();
```

Fluent request builder captures YQL + ranking in one chain:

```csharp
var request = YqlBuilder<Product>
    .Select()
    .Where(w => w.HybridSearch(p => p.Embedding, "q", "userQuery", targetHits: 100))
    .Limit(20)
    .ToSearchRequest()
    .WithRankProfile("hybrid_twophase")
    .WithQueryTensor("q", "embed(e5small, @userQuery)")
    .WithUserInput("userQuery", searchText);
```

> Full predicate reference, boolean composition, and validation rules in **[docs/yql-builder.md](https://github.com/outmatic/Vespa.NET/blob/main/docs/yql-builder.md)**

---

## Grouping & Aggregation

```csharp
var request = new VespaSearchRequest
{
    Yql = YqlBuilder<Product>
        .Select()
        .GroupBy(
            GroupingBuilder.All()
                .Group("category")
                .Max(10)
                .OrderByDescending(GroupingAgg.Count())
                .Each(e => e.Output(GroupingAgg.Count(), GroupingAgg.Avg("price"))))
        .Build(),
    Hits = 0
};

var result = await client.Search.GroupByAsync<Product>(request);
```

> Nested grouping, buckets, having, pagination, and full aggregation reference in **[docs/grouping.md](https://github.com/outmatic/Vespa.NET/blob/main/docs/grouping.md)**

---

## Bulk Feed

```csharp
// Streaming pipeline with backpressure
var result = await client.Feed.FeedAsync(
    ReadFromDatabase(),             // IAsyncEnumerable<FeedDocument<Product>>
    documentType: "product",
    maxConcurrency: 64,             // parallel HTTP/2 streams
    boundedCapacity: 256,           // backpressure buffer
    onProgress: p => Console.Write($"\r{p.SuccessCount} docs..."));
```

> Per-document conditions, BulkPut/Update/Delete, and FeedResult details in **[docs/feed.md](https://github.com/outmatic/Vespa.NET/blob/main/docs/feed.md)**

---

## Multi-Tenant Dynamic Fields

For platforms where tenants define custom fields at runtime, `[VespaExtraFields]` provides a catch-all bag — similar to MongoDB's `[BsonExtraElements]`:

```csharp
public record ProductFields
{
    // App-owned fields — strongly typed, used for ranking
    [VespaField(IndexingMode = IndexingMode.AttributeSummary)]
    public double Popularity { get; init; }

    // Tenant-defined fields — captured dynamically
    [VespaExtraFields]
    public Dictionary<string, JsonElement>? TenantFields { get; init; }
}
```

- Unmapped JSON fields are captured in the dictionary during deserialization
- Serialized flat alongside declared properties (not nested)
- Round-trip safe: deserialize then serialize preserves everything
- Schema builder ignores `[VespaExtraFields]` properties

---

## Admin API

```csharp
// Deploy from C# attributes
await client.Admin.DeploySchemaAsync<Product>();

// Multi-tenant: override tenant per-call
await client.Admin.DeploySchemaAsync<Product>(tenant: "acme");

// Deploy raw ZIP
await client.Admin.DeployAsync(zipStream);

// Cluster status
var status = await client.Admin.GetApplicationStatusAsync();
var ready   = await client.IsReadyAsync();
var version = await client.GetVersionAsync();
var metrics = await client.GetMetricsAsync();
```

> **Note:**
> `ApiKey`, `DefaultRequestHeaders`, compression, timeout, and mTLS settings are applied to both the data-plane client and the admin/config-server client.

> **Tip:**
> Health/state helpers such as `HealthCheckAsync`, `IsReadyAsync`, `GetMetricsAsync`, `GetVersionAsync`, and `GetHistogramsAsync` are best-effort on ordinary failures, but they still propagate `OperationCanceledException` when the request is cancelled.

> Schema attributes, `[VespaExtraFields]`, `ApplicationPackageOptions`, and `CustomServicesXml` in **[docs/schema.md](https://github.com/outmatic/Vespa.NET/blob/main/docs/schema.md)**

---

## Configuration & Ops

<details>
<summary><strong>DI Integration</strong></summary>

```csharp
builder.Services.AddVespaClient(new VespaClientOptions
{
    Endpoint         = "http://localhost:8080",
    DefaultNamespace = "myapp",
    EnableRetry      = true
});

// Named client (multi-cluster)
builder.Services.AddVespaClient("analytics", new VespaClientOptions
{
    Endpoint = "http://analytics-vespa:8080"
});

// Inject
public class ProductService(IVespaClient vespa)
{
    public Task<VespaDocument<Product>?> Get(string id)
        => vespa.Documents.GetAsync<Product>(id);
}
```

</details>

<details>
<summary><strong>Vespa Cloud (mTLS)</strong></summary>

```csharp
var options = new VespaClientOptions
{
    Endpoint        = "https://myapp.vespa-cloud.com",
    CertificatePath = "/path/to/cert.pem",
    ClientKeyPath   = "/path/to/key.pem",
    // or: ClientCertificate = myCert,
    // or: ApiKey = "bearer-token"
};
```

</details>

<details>
<summary><strong>Observability (OpenTelemetry)</strong></summary>

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(b => b.AddSource("Vespa.NET"))
    .WithMetrics(m => m.AddMeter("Vespa.NET"));
```

Spans: `vespa.search`, `vespa.document.*`, `vespa.feed.pipeline`, etc.
Metrics: `vespa.client.requests`, `vespa.client.request_duration`, `vespa.client.documents_written`, etc.

</details>

<details>
<summary><strong>Health Checks</strong></summary>

```csharp
builder.Services.AddHealthChecks().AddVespa();
```

</details>

<details>
<summary><strong>Testcontainers</strong></summary>

```csharp
await using var vespa = new VespaContainer();
await vespa.StartAsync();
var (client, http) = vespa.CreateClientWithHttp("test");
```

Set `VESPA_INTEGRATION_TESTS=1` to enable in CI.

</details>

> Full configuration reference, exceptions, metrics table, and more in **[docs/configuration.md](https://github.com/outmatic/Vespa.NET/blob/main/docs/configuration.md)**

---

## Documentation

| Guide | Content |
|---|---|
| **[Schema & Attributes](https://github.com/outmatic/Vespa.NET/blob/main/docs/schema.md)** | Code-first generation, `[VespaExtraFields]`, `ApplicationPackageOptions` |
| **[Document Operations](https://github.com/outmatic/Vespa.NET/blob/main/docs/document-operations.md)** | CRUD, field ops, conditional writes, visit, addressing |
| **[Search](https://github.com/outmatic/Vespa.NET/blob/main/docs/search.md)** | Basic, nearest-neighbor, hybrid, streaming, request options |
| **[YQL Builder](https://github.com/outmatic/Vespa.NET/blob/main/docs/yql-builder.md)** | Fluent query builder, predicates, boolean composition |
| **[Grouping](https://github.com/outmatic/Vespa.NET/blob/main/docs/grouping.md)** | Aggregations, buckets, nested grouping, pagination |
| **[Ranking DSL](https://github.com/outmatic/Vespa.NET/blob/main/docs/ranking.md)** | RankingBuilder, code-first rank profiles |
| **[Feed](https://github.com/outmatic/Vespa.NET/blob/main/docs/feed.md)** | Bulk operations, streaming pipeline, backpressure |
| **[Configuration](https://github.com/outmatic/Vespa.NET/blob/main/docs/configuration.md)** | Options, DI, mTLS, observability, health checks, testing |

---

## Running the Sample App

```bash
docker run --detach --name vespa --publish 8080:8080 --publish 19071:19071 vespaengine/vespa
dotnet run --project samples/Vespa.NET.Samples
```

---

## Dependencies

| Package | Purpose |
|---|---|
| `Microsoft.Extensions.Http` | `IHttpClientFactory` |
| `Microsoft.Extensions.Http.Resilience` | Retry + circuit breaker (Polly) |
| `Microsoft.Extensions.Diagnostics.HealthChecks` | ASP.NET Core health check |
| `System.Text.Json` | JSON serialization (built-in) |
| `DotNet.Testcontainers` *(test project only)* | Docker test fixtures |

---

## Resources

- [Vespa.ai Documentation](https://docs.vespa.ai/)
- [YQL Reference](https://docs.vespa.ai/en/reference/querying/yql.html)
- [Document API Reference](https://docs.vespa.ai/en/reference/api/document-v1.html)

## License

MIT License — see [LICENSE](https://github.com/outmatic/Vespa.NET/blob/main/LICENSE) for details.
