# Vespa.NET.VectorData

Use an existing Vespa application through the standalone
`Microsoft.Extensions.VectorData` abstractions: write typed records, retrieve
them by key, and search their vectors with application-defined filters. The same
collection can supply retrieval tools to Microsoft Agent Framework.

**Version `1.0.0` is being prepared; not yet published to NuGet.** This is an optional,
separate package for .NET 8 and .NET 10 with a restricted VectorData implementation.
It depends on `Vespa.NET` and `Microsoft.Extensions.VectorData.Abstractions`.
It does not depend on Semantic Kernel, Agent Framework, or an embedding provider.
Installing the base `Vespa.NET` client does not install this connector.

## Try it from source

Use a checkout containing `src/Vespa.NET.VectorData`. Run these commands from the
repository root to create a console application beside the checkout:

```sh
dotnet new console --output ../VespaVectorQuickstart --framework net10.0
dotnet add ../VespaVectorQuickstart reference src/Vespa.NET.VectorData/Vespa.NET.VectorData.csproj
```

The connector also targets `net8.0`; the Agent Framework sample below requires
.NET 10. The project reference is the simplest way to try the unpublished code.
To produce a local NuGet package instead, pack only the connector:

```sh
dotnet pack src/Vespa.NET.VectorData -c Release -o artifacts/packages
```

In a separate consumer project, add the absolute path to `artifacts/packages` as
a NuGet source alongside nuget.org, then install `Vespa.NET.VectorData` with
`--version 1.0.0`. The client dependency (`Vespa.NET >= 3.1.0`) and other
dependencies are restored from nuget.org. These commands build the package
locally and do not publish it. The connector has its own version; it does not
follow the base client's version number.

## Deploy the schema first

A collection name is an existing Vespa schema/document type. Calling
`GetCollection` binds a C# model to it; it neither creates nor validates the
deployed schema. Collection creation, deletion, discovery, and existence checks
throw `NotSupportedException`.
Applications that automatically call `EnsureCollectionExistsAsync` must skip that
step for this provider and arrange deployment separately.

For this quickstart, use the checked-in
[application package](https://github.com/outmatic/Vespa.NET/tree/main/samples/Vespa.NET.AgentFramework/app)
with its `article` schema and 768-dimensional vector. Use a dedicated development
instance: deploying an application replaces that instance's application configuration.
Install Docker and the Vespa CLI using the
[official local deployment guide](https://docs.vespa.ai/en/basics/deploy-an-application-local.html).
With ports 8080 and 19071 available, run from the repository root:

```sh
docker run --detach --name vespa-vector-data --hostname vespa-container \
  --publish 127.0.0.1:8080:8080 --publish 127.0.0.1:19071:19071 \
  vespaengine/vespa:8
vespa deploy samples/Vespa.NET.AgentFramework/app -t local --wait 300
```

Port 8080 serves document and search requests; 19071 serves application deployment.
The [`vespa deploy` command](https://docs.vespa.ai/en/reference/clients/vespa-cli/vespa_deploy.html)
uploads, validates, and activates the complete package. For an existing deployment,
adapt its application package separately to satisfy the contract below.

## Write, read, and search

Replace `../VespaVectorQuickstart/Program.cs` with this complete example. Its
deterministic vectors exercise the API without downloading an embedding model;
they do not encode the meaning of the titles.

```csharp
using Microsoft.Extensions.VectorData;
using Vespa;
using Vespa.VectorData;

using var client = new VespaClient(new VespaClientOptions
{
    Endpoint = Environment.GetEnvironmentVariable("VESPA_ENDPOINT") ?? "http://localhost:8080"
});
using var store = new VespaVectorStore(client, new()
{
    Namespace = "knowledge",
    RankProfile = "vector_search",
    Approximate = false // Exact search makes this tiny example predictable.
});
using var articles = store.GetCollection<string, Article>("article");
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var ct = cancellation.Token;

while (!await client.IsReadyAsync(ct))
    await Task.Delay(500, ct);

await articles.UpsertAsync(new Article
{
    Id = "quickstart-search",
    Title = "Vector search in Vespa",
    Embedding = UnitVector(0)
}, ct);
await articles.UpsertAsync(new Article
{
    Id = "quickstart-deploy",
    Title = "Deploying a Vespa application",
    Embedding = UnitVector(1)
}, ct);

var saved = await articles.GetAsync("quickstart-search",
    new() { IncludeVectors = true }, ct);
Console.WriteLine($"Read: {saved?.Title}, dimensions: {saved?.Embedding.Length}");

// Search visibility can lag a successful document write. Wait for both demo records.
var visible = 0;
while (visible < 2)
{
    visible = 0;
    await foreach (var record in articles.GetAsync(a => a.Category == "quickstart", 2,
        cancellationToken: ct))
        visible++;
    if (visible < 2)
        await Task.Delay(250, ct);
}

await foreach (var hit in articles.SearchAsync(UnitVector(0), 2, new()
{
    Filter = a => a.Published && a.Category == "quickstart",
    IncludeVectors = true
}, ct))
    Console.WriteLine($"{hit.Record.Id}: {hit.Record.Title} (cosine: {hit.Score:F3})");

// Upsert replaces the record. Delete removes it by its caller-assigned key.
await articles.DeleteAsync("quickstart-search", ct);
await articles.DeleteAsync("quickstart-deploy", ct);

static ReadOnlyMemory<float> UnitVector(int index)
{
    var vector = new float[768];
    vector[index] = 1;
    return vector;
}

public sealed class Article
{
    [VectorStoreKey]
    public string Id { get; set; } = "";

    [VectorStoreData(StorageName = "title")]
    public string Title { get; set; } = "";

    [VectorStoreData(StorageName = "category", IsIndexed = true)]
    public string Category { get; set; } = "quickstart";

    [VectorStoreData(StorageName = "priority", IsIndexed = true)]
    public int Priority { get; set; } = 1;

    [VectorStoreData(StorageName = "published", IsIndexed = true)]
    public bool Published { get; set; } = true;

    [VectorStoreVector(768, StorageName = "embedding",
        DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
```

Run it from the repository root:

```sh
dotnet run --project ../VespaVectorQuickstart
```

The two search results should have cosine scores of approximately `1` and `0`.
The example deletes its two documents when it finishes. Keys are the user portion
of a Vespa document ID: `quickstart-search` becomes
`id:knowledge:article::quickstart-search`. The key is not a stored schema field.

Vector properties are cleared to `null` for arrays or empty memory unless
`IncludeVectors = true` is requested on reads or searches. `GetAsync(key)` returns `null` for a missing
document. Enumerable upsert/get/delete overloads process individual documents;
batch writes are sequential and are not transactions or the client's bulk-feed
pipeline. A write response reporting ignored fields raises an exception, but the
server may already have stored the remaining fields. A batch can also fail after
earlier records were written; failures do not imply rollback.

## Schema and mapping contract

Use VectorData attributes as above, or pass a `VectorStoreCollectionDefinition`
to `GetCollection`. Storage names are explicit: without `StorageName`, the CLR
property name is used unchanged. These attributes do not generate Vespa schemas.
`IsFullTextIndexed` and `IndexKind` metadata do not provision or validate server
indexes; configure the deployed schema yourself.

The [sample schema](https://github.com/outmatic/Vespa.NET/blob/main/samples/Vespa.NET.AgentFramework/app/schemas/article.sd)
provides the complete contract. In your own application:

- Use one caller-assigned string key and exactly one `float[]` or
  `ReadOnlyMemory<float>` vector. Records need a public parameterless constructor
  and public readable/writable mapped properties.
- Match the vector dimensions to a dense `tensor<float>` field with `angular`
  distance. Enable `attribute`; enable `index` for HNSW; include `summary` to
  retrieve vectors in search results. Input vectors must be finite and nonzero.
- Declare the query tensor with the same dimension name and size as the stored
  tensor. Rank only by angular proximity and return its distance as a match feature:

```text
rank-profile vector_search {
    inputs {
        query(query_embedding) tensor<float>(x[768])
    }
    first-phase {
        expression: closeness(field, embedding)
    }
    match-features: distance(field, embedding)
}
```

- Expose mapped data fields in summaries. Data properties support `string`,
  `bool`, `int`, `long`, `float`, `double`, nullable scalars, and scalar arrays.
  Nested objects and dynamic records are unsupported.
- Mark filterable scalar properties `IsIndexed = true` and configure suitable
  Vespa attributes. String equality needs a **single-valued string attribute with
  `match: cased`**, as shown by the sample's `category` field.
- **Dedicate each connected schema to one document namespace.** CRUD addresses
  the configured namespace, but search spans namespaces. The connector rejects
  hits with another namespace/schema or group/number addressing; this is not
  server-side tenant isolation.

Vespa's [angular metric](https://docs.vespa.ai/en/reference/schemas/schemas.html#angular)
returns an angle. The connector exposes `cos(distance)` as the result score,
between -1 and 1, with larger values indicating closer vectors. It does not use
raw Vespa relevance as a cosine score. Unrelated reranking would violate the
ordering contract.

## Filters, paging, and options

Search filters and filtered `GetAsync` support scalar comparisons, `&&`, `||`,
`!`, constants, and captured local values. String comparisons support equality
and inequality with case preserved. Null/missing-field predicates, method calls
such as `StartsWith`, collection membership, and ordered filtered retrieval are
unsupported. Empty-string predicates are unsupported because Vespa does not
index empty strings. Array properties cannot be used as scalar filters. Unsupported or
meaning-changing casts are rejected. Filtering is not full-text search, and
invalid filters are rejected before generating a query embedding.

`top` and `Skip` select a bounded result window. `ScoreThreshold` applies to the
retrieved window and may return fewer than `top` results; it does not fetch
replacement hits. Backend execution failures, query errors, and incomplete search
coverage raise contextual `VectorStoreException` errors. Caller cancellation
remains cancellation; unsupported operations and invalid arguments fail locally.

| `VespaVectorStoreOptions` | Meaning |
|---|---|
| `Namespace` | Required document namespace; dedicate the schema to it |
| `RankProfile` | Required deployed angular-proximity profile with distance match feature |
| `QueryTensorName` | Query input name; default `query_embedding` |
| `Approximate` | Use approximate nearest-neighbor search; default `true` |
| `MaxResults` | Maximum `top + Skip`; default `400`. Vespa's query profile must also permit the requested hits and offset |
| `SearchTimeout` | Vespa per-query timeout; default five seconds. The HTTP client's timeout applies independently |
| `EmbeddingGenerator` | Optional caller-owned `IEmbeddingGenerator<string, Embedding<float>>` for text queries |

The caller owns the `IVespaClient` and embedding generator. Keep them alive for
all collections that use them and dispose them yourself; disposing a collection
or store does not dispose either dependency. Configure endpoints, authentication,
HTTP resilience, and telemetry through the base client.

This provider implements only part of the VectorData surface. Schema lifecycle,
schema discovery/existence, other key or vector types, other distance metrics,
multiple vectors, deep export, and hybrid search interfaces are unsupported.
Use the [base Vespa.NET client](https://github.com/outmatic/Vespa.NET) for native
hybrid search, custom ranking, bulk feed, document visiting, and deployment.

## Real embeddings and Microsoft Agent Framework

The runnable
[Agent Framework sample](https://github.com/outmatic/Vespa.NET/tree/main/samples/Vespa.NET.AgentFramework)
uses OllamaSharp, `nomic-embed-text`, and a tool-capable chat model. Start Ollama,
deploy the application above, then run from the repository root:

```sh
ollama pull nomic-embed-text
ollama pull qwen3:4b
dotnet run --project samples/Vespa.NET.AgentFramework -- --seed "How does vector search work?"
```

`--seed` explicitly generates embeddings, writes three sample documents in the
`agent-framework` category, and waits until their IDs are searchable.
Omit it on subsequent runs. Configure `VESPA_ENDPOINT`, `OLLAMA_ENDPOINT`, and
`OLLAMA_MODEL` to change the defaults (`http://localhost:8080`,
`http://localhost:11434`, and `qwen3:4b`).

The sample constructs an `OllamaApiClient`, uses it as
`IEmbeddingGenerator<string, Embedding<float>>`, and supplies it in store options.
It generates document vectors before `UpsertAsync`; **upsert never generates
embeddings**. With a generator configured, `SearchAsync(string, ...)` generates
the query vector. Vector inputs bypass that generator.
An explicitly configured generator must support
`IEmbeddingGenerator<string, Embedding<float>>`; incompatible generators are
rejected when the collection is created.

Use the same embedding model, dimension, and model-appropriate preprocessing for
feeding and querying. The sample follows Nomic's
[retrieval prefixes](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5):
`search_document: ` for passages and `search_query: ` for queries. The connector
adds neither prefix. Do not mix the quickstart's artificial vectors with real
document embeddings; the quickstart removes its records on completion.

In [Program.cs](https://github.com/outmatic/Vespa.NET/blob/main/samples/Vespa.NET.AgentFramework/Program.cs),
`AIFunctionFactory.Create` wraps collection search as the `search_vespa` tool.
The function filters by `Published` and the sample category in application code and returns source
IDs with passages. `chat.AsAIAgent(..., tools: [search])` registers that function,
and `agent.RunAsync(...)` lets the model call it. This is an application tool
integration; the connector itself needs no Agent Framework dependency.
Retrieval code that needs only search can accept `IVectorSearchable<Article>`
instead of the concrete collection, leaving collection management to the caller.

## Verify the connector

From the repository root:

```sh
dotnet test tests/Vespa.NET.VectorData.Tests -c Release
VESPA_VECTOR_DATA_INTEGRATION_TESTS=1 dotnet test tests/Vespa.NET.VectorData.Tests -c Release --filter FullyQualifiedName~LiveVespaTests
```

The default suite tests HTTP request bodies, mapping, filters, scores, errors,
cancellation, ownership, and a real Agent Framework tool loop with deterministic
chat/embedding test doubles. It does not download or run models.

The opt-in Docker test creates its own Vespa container, deploys a three-dimensional
test schema, and exercises the connector and retrieval tool against it. It is
reported skipped unless enabled and never deploys to an existing instance.
Set `VESPA_VECTOR_DATA_IMAGE` to select the test image.
