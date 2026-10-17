<div align="center">

# Vespa.NET

**Typed document, search and feed APIs for [Vespa.ai](https://vespa.ai) on .NET 8 and .NET 10.**

[![NuGet](https://img.shields.io/nuget/v/Vespa.NET?style=flat-square&logo=nuget&color=004880)](https://www.nuget.org/packages/Vespa.NET)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512bd4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)
[![.NET 8](https://img.shields.io/badge/.NET-8.0_(LTS)-512bd4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-green?style=flat-square)](https://github.com/outmatic/Vespa.NET/blob/main/LICENSE)

</div>

---

Use C# models for document serialization and schema generation, compose queries
with the YQL builder, and feed mixed document operations with bounded concurrency.

[Quick Start](#quick-start) · [Documents](#model-aware-api) · [Search](#yql-builder) ·
[Grouping](#grouping--aggregation) · [Feed](#bulk-feed) · [Configuration](#configuration--ops) ·
[Guides](#documentation)

## Features

| | |
|---|---|
| **Schema** | Code-first `.sd` generation from attributes, multi-type deploy, [custom `services.xml`](https://github.com/outmatic/Vespa.NET/blob/main/docs/schema.md) |
| **Documents** | CRUD, conditional writes, field-level ops, resumable visit pages, group/number addressing (read+write), selection-based update/delete/copy with continuation auto-loop + crash-safe manual paging |
| **Search** | Full-text, nearest-neighbor, hybrid, streaming-mode queries and lazy result pagination |
| **YQL** | Fluent type-safe builder with boolean composition, grouping, ranking DSL |
| **Feed** | Mixed puts/updates/deletes across schemas and namespaces, per-document ordering, bounded backpressure, progress callbacks |
| **Models** | Attribute-based field names, tensor formats and [`[VespaExtraFields]`](https://github.com/outmatic/Vespa.NET/blob/main/docs/schema.md#dynamic-fields-with-vespaextrafields) for fields outside the C# model |
| **Cloud** | mTLS (PEM, PFX, in-memory cert), Bearer token auth |
| **Ops** | OpenTelemetry traces + metrics, ASP.NET Core health checks, Testcontainers |
| **Performance** | HTTP/2 multiplexing, connection pooling, GZip/Deflate, `ResponseHeadersRead` |
| **Resilience** | HTTP retry + circuit breaker through DI, feed-level retry and source-generated logging |

---

## Quick Start

The example targets a local Vespa 8 instance with the document/query endpoint on
port `8080` and the config server on port `19071`. It deploys a generated
application package; use a separate development instance for this example.

```bash
docker run --detach --name vespa --publish 8080:8080 --publish 19071:19071 vespaengine/vespa:8
```

Wait for the config server at `http://localhost:19071/ApplicationStatus` to respond
before deploying. If your application is already deployed, configure `Endpoint`
and skip the deployment call.

### 1. Install

```bash
dotnet new console --name VespaQuickstart --framework net10.0
cd VespaQuickstart
dotnet add package Vespa.NET
```

Use `--framework net8.0` to target .NET 8.

| Package | Use |
|---|---|
| [`Vespa.NET`](https://www.nuget.org/packages/Vespa.NET) | Client, DI integration, YQL and schema builders; includes `Vespa.NET.Models` transitively |
| [`Vespa.NET.Models`](https://www.nuget.org/packages/Vespa.NET.Models) | DTOs, attributes and tensor types without client dependencies |
| [`Vespa.NET.Testcontainers`](https://www.nuget.org/packages/Vespa.NET.Testcontainers) | Optional Docker helper for integration tests |

### 2. Define a model

Add `Product.cs`:

```csharp
using Vespa.Models.Attributes;
using Vespa.Models.Schema;
using Vespa.Models.Tensors;

[VespaDocument("product", Namespace = "myapp")]
[VespaRankProfile("semantic", Inherits = "default", FirstPhase = "closeness(field, embedding)")]
[VespaRankProfile("hybrid", Inherits = "default",
    FirstPhase = "closeness(field, embedding) + nativeRank(product_name)")]
public record Product
{
    [VespaId]
    public string Id { get; init; } = "";

    [VespaField(Name = "product_name", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Name { get; init; } = "";

    [VespaField(Name = "price", IndexingMode = IndexingMode.AttributeSummary)]
    public decimal Price { get; init; }

    [VespaField(Name = "category", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Category { get; init; } = "";

    [VespaTensor("tensor<float>(x[4])", EnableIndex = true, DistanceMetric = DistanceMetric.Euclidean)]
    public VespaTensor Embedding { get; init; } = null!;
}
```

### 3. Deploy, index, search

Add the following to `Program.cs`. The four-dimensional vectors keep the example
self-contained; in an application, use your embedding model's vectors and dimension.

```csharp
using Vespa;
using Vespa.Documents;
using Vespa.Models;
using Vespa.Models.Tensors;
using Vespa.Search;

var options = new VespaClientOptions
{
    Endpoint             = "http://localhost:8080",
    ConfigServerEndpoint = "http://localhost:19071",
    DefaultNamespace     = "myapp"
};

using var client = new VespaClient(options);

// Deploy schema from C# attributes
await client.Admin.DeploySchemaAsync<Product>();

// Allow the application container to become ready after activation
using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
while (!await client.IsReadyAsync(startup.Token))
    await Task.Delay(TimeSpan.FromMilliseconds(500), startup.Token);

// Index a document
var product = new Product
{
    Id = "p-1",
    Name = "Laptop",
    Price = 999.99m,
    Category = "electronics",
    Embedding = VespaTensor.FromDenseValues<float>([0.1f, 0.2f, 0.3f, 0.4f])
};
await client.Documents.PutAsync(product.Id, product);

// Retrieve it directly, or search with the generated semantic rank profile
var document = await client.Documents.GetAsync<Product>(product.Id);
Console.WriteLine(document?.Fields?.Name);

var queryEmbedding = VespaTensor.FromDenseValues<float>([0.1f, 0.2f, 0.3f, 0.5f]);
var results = await client.Search.NearestNeighborSearchAsync<Product>(
    queryEmbedding, p => p.Embedding, topK: 10, rankProfile: "semantic");

foreach (var hit in results.Root.Children)
    Console.WriteLine($"{hit.Fields?.Name}: {hit.Relevance:F3}");
```

Writes can become visible to search after the write response; a direct document
GET and a search do not have the same visibility guarantees.

The options-only constructor configures its HTTP handler and owns its lifetime.
For factory-managed HTTP retry and circuit breaking, use
[`AddVespaClient`](#configuration--ops). Feed-level retry is also available with
a directly constructed client.

---

## Model-Aware API

The following examples are independent and reuse the model and client above.
Document extensions infer the schema and namespace from `[VespaDocument]`;
search extensions infer the schema. `[VespaField(Name)]` drives both schema
generation and JSON serialization.

```csharp
// CRUD — no documentType needed
await client.Documents.PutAsync(product.Id, product);
var doc = await client.Documents.GetAsync<Product>(product.Id);

// Request options with model-inferred schema and namespace
var readOptions = new DocumentRequestOptions { Cluster = "content", Timeout = TimeSpan.FromSeconds(5) };
var routedDoc = await client.Documents.GetAsync<Product>(readOptions, product.Id);

// Field-level update with typed lambda builder
await client.Documents.UpdateFieldsAsync<Product>(product.Id, ops => ops
    .Field(p => p.Name, FieldOp.Assign("New Name"))
    .Field(p => p.Price, FieldOp.Multiply(0.9)));

// Nearest-neighbor — field name resolved via lambda
var results = await client.Search.NearestNeighborSearchAsync<Product>(
    queryEmbedding, p => p.Embedding, topK: 10, rankProfile: "semantic");

// Visit
await foreach (var d in client.Documents.VisitAsync<Product>(selection: "product.price > 100"))
    Console.WriteLine(d.Fields?.Name);

// One visit page; persist Continuation after processing to resume later
var page = await client.Documents.VisitPageAsync<Product>(cluster: "content");
foreach (var visited in page.Documents)
    Console.WriteLine(visited.Fields?.Name);

if (!page.IsComplete)
    page = await client.Documents.VisitPageAsync<Product>(
        cluster: "content", continuation: page.Continuation);

// Selection-based bulk ops — typed builder, auto-loops on Vespa's continuation token
var resp = await client.Documents.UpdateBySelectionAsync<Product>(
    "product.price > 100",
    ops => ops.Field(p => p.Price, FieldOp.Multiply(0.9)),
    cluster: "content");
// resp.DocumentCount is the total across all chunks

await client.Documents.DeleteAsync<Product>(product.Id);
```

Persist a visit continuation **after processing that page**. An empty page may
still have a continuation; use `IsComplete` to decide whether visiting has ended.

Pass a bare ID such as `"p-1"` or a full ID such as `"id:myapp:product::p-1"`.
Full IDs retain their namespace, type and group/number addressing; incompatible
targets throw. Returned documents expose `FullId` as well as the user-specified `Id`.

---

## YQL Builder

```csharp
using Vespa.Query;

var yql = YqlBuilder<Product>
    .Select(p => p.Name, p => p.Price)
    .Where(w =>
    {
        w.Field(p => p.Price).GreaterThan(10);
        w.Field(p => p.Name).Contains("laptop");
    })
    .OrderBy(p => p.Price, descending: true)
    .Limit(20)
    .Build();
```

Fluent request builder captures YQL + ranking in one chain:

```csharp
var request = YqlBuilder<Product>
    .Select()
    .Where(w => w.HybridSearch(p => p.Embedding, "query_embedding", "text", totalTargetHits: 100))
    .Limit(20)
    .ToSearchRequest()
    .WithRankProfile("hybrid")
    .WithQueryTensor("query_embedding", queryEmbedding)
    .WithUserInput("text", "laptop");

var results = await client.Search.SearchAsync<Product>(request);
```

`HybridSearch` retrieves with nearest-neighbor search and uses text matches for
ranking through Vespa's `rank()` operator. The example's `hybrid` rank profile is
declared on `Product` above. `embed(...)` inputs are also supported when you have
configured an embedder in Vespa.

Additional query parameters can be sent with `request.WithParameter(name, value)`.
For example, `request.WithParameter("ranking.matching.anntimebudget", "25ms")`
sets an ANN time budget on Vespa 8.684.9 or newer.

`SearchStreamAsync` paginates ordinary search results lazily and is subject to
Vespa's `maxHits`/`maxOffset` limits. Use document visiting to export a corpus.
Vespa's separate streaming search mode requires a streaming-mode content cluster
and request properties such as `StreamingGroupName`.

> Full predicate reference, boolean composition, and validation rules in **[docs/yql-builder.md](https://github.com/outmatic/Vespa.NET/blob/main/docs/yql-builder.md)**

---

## Grouping & Aggregation

```csharp
using Vespa.Query;

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

var result = (await client.Search.GroupByAsync<Product>(request)).AsTyped();
foreach (var group in result.GroupingResults[0].Groups)
    Console.WriteLine($"{group.Value}: {group.GetAggregation<long>("count()")} documents");
```

> Typed grouping, exact aggregation values, buckets, summaries and pagination in **[docs/grouping.md](https://github.com/outmatic/Vespa.NET/blob/main/docs/grouping.md)**

---

## Bulk Feed

```csharp
using Vespa.Feed;

// Mixed operations with per-document ordering
FeedOperation[] operations =
[
    FeedOperation.Put("p-1", product),
    FeedOperation.Update<Product>("p-1", update => update.Field(p => p.Price, FieldOp.Assign(49.99m))),
    FeedOperation.Delete<Product>("p-2")
];
var mixed = await client.Feed.FeedAsync(operations, new FeedOptions { MaxConcurrency = 32 });
Console.WriteLine($"{mixed.SuccessCount}/{mixed.TotalOperations} operations succeeded");

foreach (var error in mixed.Errors)
    Console.WriteLine($"{error.OperationIndex}: {error.DocumentId} — {error.Message}");
```

The mixed feed accepts both `IEnumerable<FeedOperation>` and
`IAsyncEnumerable<FeedOperation>`. `FeedOptions.BoundedCapacity` limits buffered
work; operations for the same document preserve source order, including retries.

> Mixed operations, ordering, per-document conditions, BulkPut/Update/Delete, and FeedResult details in **[docs/feed.md](https://github.com/outmatic/Vespa.NET/blob/main/docs/feed.md)**

---

## Fields Outside the C# Model

Use `[VespaExtraFields]` to preserve Vespa fields without declaring a C# property
for each one. These fields must still exist in your deployed Vespa schema.

```csharp
using System.Text.Json;
using Vespa.Models.Attributes;
using Vespa.Models.Schema;

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
- Values are retained as `JsonElement` for round trips
- The schema builder skips the dictionary; declare its fields in the Vespa schema separately

---

## Admin API

```csharp
// Deploy from C# attributes
await client.Admin.DeploySchemaAsync<Product>();

// Multi-tenant: override tenant per-call
await client.Admin.DeploySchemaAsync<Product>(tenant: "acme");

// Deploy raw ZIP
using var zipStream = File.OpenRead("application.zip");
await client.Admin.DeployAsync(zipStream);

// Application status and readiness
var status = await client.Admin.GetApplicationStatusAsync();
var ready   = await client.IsReadyAsync();
var version = await client.GetVersionAsync();
var metrics = await client.GetMetricsAsync();
```

> **Note:**
> `ApiKey`, `DefaultRequestHeaders`, compression, timeout, and mTLS settings are applied to both the data-plane client and the admin/config-server client.

> **Note:**
> Retry and circuit-breaker options are enforced by the DI registrations (`AddVespaClient`). A directly constructed `VespaClient` applies feed-level retry only; use `new VespaClient(options)` (options-only constructor) to get a fully configured handler (mTLS, compression, pooling) without DI.

> **Tip:**
> Health/state helpers such as `HealthCheckAsync`, `IsReadyAsync`, `GetMetricsAsync`, `GetVersionAsync`, and `GetHistogramsAsync` are best-effort on ordinary failures, but they still propagate `OperationCanceledException` when the request is cancelled.

> Schema attributes, `[VespaExtraFields]`, `ApplicationPackageOptions`, and `CustomServicesXml` in **[docs/schema.md](https://github.com/outmatic/Vespa.NET/blob/main/docs/schema.md)**

---

## Configuration & Ops

<details>
<summary><strong>DI Integration</strong></summary>

In an ASP.NET Core application, register the client before building the app:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Vespa;
using Vespa.Documents;
using Vespa.Models;

var builder = WebApplication.CreateBuilder(args);

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
    public Task<VespaDocument<Product>?> GetAsync(string id)
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

Install `OpenTelemetry.Extensions.Hosting` and register the SDK's sources in your
OpenTelemetry setup:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithTracing(b => b.AddSource("Vespa.NET"))
    .WithMetrics(m => m.AddMeter("Vespa.NET"));
```

Spans: `vespa.search`, `vespa.document.*`, `vespa.feed.pipeline`, etc.
Query text (YQL) and document ids are excluded from span tags by default; opt in with `VespaClientOptions.RecordQueryTextInTraces = true`.
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
using Vespa.Testcontainers;

await using var vespa = new VespaContainer();
await vespa.StartAsync();
var client = vespa.CreateClient("test");
await client.Admin.DeploySchemaAsync<Product>();
```

Install `Vespa.NET.Testcontainers` and have Docker running. The container owns the
client created by `CreateClient`. To run **this repository's** integration suite,
set `VESPA_INTEGRATION_TESTS=1`; this flag is not required by the helper itself.

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

The sample app uses the local Vespa instance described in Quick Start:

```bash
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
| `Testcontainers` *(optional helper package)* | Docker fixtures through `Vespa.NET.Testcontainers` |

---

## Resources

- [Vespa.ai Documentation](https://docs.vespa.ai/)
- [YQL Reference](https://docs.vespa.ai/en/reference/querying/yql.html)
- [Document API Reference](https://docs.vespa.ai/en/reference/api/document-v1.html)

## License

MIT License — see [LICENSE](https://github.com/outmatic/Vespa.NET/blob/main/LICENSE) for details.
