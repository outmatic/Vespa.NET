# Vespa.NET.VectorData

Use an existing Vespa application through the standalone
`Microsoft.Extensions.VectorData` abstractions: write typed or dictionary records, retrieve
them by key, and search their vectors with application-defined filters. The same
collection can supply retrieval tools to Microsoft Agent Framework, including
application-defined preference tools and Microsoft's `ChatHistoryMemoryProvider`.

This optional package targets .NET 8 and .NET 10. Its dependencies are `Vespa.NET`
and `Microsoft.Extensions.VectorData.Abstractions`. Agent Framework and the
embedding provider are configured by the consuming application.

## Install

```sh
dotnet add package Vespa.NET.VectorData --version 1.1.0
```

The connector has its own version, independent of the base client. See the
[release notes](https://github.com/outmatic/Vespa.NET/blob/main/src/Vespa.NET.VectorData/CHANGELOG.md)
for its supported surface and examples.

The defaults and options documented below describe unreleased 1.2.0. Build from
this checkout to use them; 1.1.0 remains the published NuGet version and does not
provide the same summary defaults or bounded batch options.

## Build from source

Run these commands from the repository root to create a console application
beside the checkout:

```sh
dotnet new console --output ../VespaVectorQuickstart --framework net10.0
dotnet add ../VespaVectorQuickstart reference src/Vespa.NET.VectorData/Vespa.NET.VectorData.csproj
```

The connector also targets `net8.0`; the Agent Framework sample below requires
.NET 10.
To produce a local NuGet package instead, pack only the connector:

```sh
dotnet pack src/Vespa.NET.VectorData -c Release -o artifacts/packages
```

In a separate consumer project, add the absolute path to `artifacts/packages` as
a NuGet source alongside nuget.org, then install `Vespa.NET.VectorData` with
`--version 1.2.0`. The client dependency (`Vespa.NET >= 3.1.1`) and other
dependencies are restored from nuget.org.

## Deploy the schema first

A collection name is an existing Vespa schema/document type. Calling
`GetCollection` binds a C# model to it; it neither creates nor validates the
deployed schema. `EnsureCollectionExistsAsync` checks that the schema is searchable
using a read-only query and caches a successful check for that collection instance.
It does not deploy schemas or validate every field and rank profile. An undeployed
schema fails the check. Creation, deletion, discovery and `CollectionExistsAsync`
remain unsupported; arrange application deployment separately.

For this quickstart, use the checked-in
[application package](https://github.com/outmatic/Vespa.NET/tree/main/samples/Vespa.NET.AgentFramework/app)
with its `article` schema and 768-dimensional vector. Use a dedicated development
instance: deploying an application replaces that instance's application configuration.
Install Docker using the
[official local deployment guide](https://docs.vespa.ai/en/basics/deploy-an-application-local.html).
With ports 8080 and 19071 available, run from the repository root:

```sh
docker run --detach --name vespa-vector-data --hostname vespa-container \
  --publish 127.0.0.1:8080:8080 --publish 127.0.0.1:19071:19071 \
  vespaengine/vespa:8
```

The Agent Framework sample can deploy this application itself with `--deploy`;
see below. For the standalone C# quickstart, install the Vespa CLI and run:

```sh
vespa deploy samples/Vespa.NET.AgentFramework/app -t local --wait 300
```

Port 8080 serves document and search requests; 19071 serves application deployment.
The [`vespa deploy` command](https://docs.vespa.ai/en/reference/clients/vespa-cli/vespa_deploy.html)
uploads, validates, and activates the complete package. For an existing deployment,
adapt its application package separately to satisfy the contract below.

## Write, read, and search

Replace `../VespaVectorQuickstart/Program.cs` with this example. It uses
deterministic unit vectors and requires no embedding model.

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
    RankProfile = "vector_search"
});
using var articles = store.GetCollection<string, Article>("article");
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var ct = cancellation.Token;

while (!await client.IsReadyAsync(ct))
    await Task.Delay(500, ct);

await articles.EnsureCollectionExistsAsync(ct);

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

// Wait for search visibility after the document writes.
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
`IncludeVectors = true` is requested on reads or searches. A requested vector that
is absent, null or invalid raises an error. `GetAsync(key)` returns `null` for a
missing document, and requests only the mapped document fields it needs through
`fieldSet`, excluding the vector unless requested. Batch writes use bounded chunks
and concurrency; they are not transactions or the client's bulk-feed pipeline.
A write response reporting ignored fields raises an exception, but the server may
already have stored the remaining fields. Failures never imply rollback.

### Bounded writes and partial outcomes

`UpsertBatchSize` limits buffered records per chunk (default `64`),
`EmbeddingBatchSize` limits text inputs per embedding request (default `32`), and
`UpsertConcurrency` limits concurrent document writes (default `4`). Each chunk
is mapped and its generated vectors validated before writing that chunk. Validation
or embedding failure in a later chunk does not undo earlier chunks. Writes of the
same key preserve input order, including duplicate keys; different keys can run
concurrently. Payload buffering is bounded, while outcome metadata grows with the
number of consumed records.

Write failures and failures after earlier chunks were submitted throw
`VespaVectorUpsertException`. Before any write, validation and embedding errors
propagate without a partial-write report. Caller cancellation
throws `VespaVectorUpsertCanceledException`, which remains an
`OperationCanceledException`. Both expose `Outcomes` and `InputFullyEnumerated`.
Each outcome contains the original zero-based `Index`, the `Key` when available,
a `Status`, and an optional `Error`:

| Status | Meaning |
|---|---|
| `Succeeded` | Vespa acknowledged the write |
| `Unconfirmed` | A write was attempted but failed or timed out; it may already have been applied |
| `NotAttempted` | The consumed record was not submitted for writing |

Outcomes preserve indices and duplicate keys. Records the enumerable has not
yielded are not listed; `InputFullyEnumerated = false` means the tail is unknown.
Use the outcomes to reconcile the attempted writes before retrying. Do not treat
`Unconfirmed` as proof that a document is absent or replay an entire batch assuming
it was rolled back. This API uses document PUTs, so retrying a stale value can
overwrite a newer application write.

## Schema and mapping contract

Use VectorData attributes as above, or pass a `VectorStoreCollectionDefinition`
to `GetCollection`. Storage names are explicit: without `StorageName`, the CLR
property name is used unchanged. These attributes do not generate Vespa schemas.
`IsFullTextIndexed` and `IndexKind` metadata do not provision or validate server
indexes; configure the deployed schema yourself.

The [sample schema](https://github.com/outmatic/Vespa.NET/blob/main/samples/Vespa.NET.AgentFramework/app/schemas/article.sd)
provides the complete contract. In your own application:

- Use one caller-assigned `string` or `Guid` key and exactly one `float[]` or
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
  Nested objects are unsupported. Dictionary records use an explicit definition,
  as described below.
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

### Filter indexes and summaries

For frequently used selective filters, such as a user or session ID, use
`attribute: fast-search` to build posting lists. Add `rank: filter` when a field
only restricts matching and does not contribute term-based ranking. The sample
uses these settings on `category`, `user_id`, `UserId` and `SessionId`.
Indexing every metadata field increases RAM and write costs; choose indexes for
the filters your application uses. See [Vespa's tuning guidance](https://docs.vespa.ai/en/performance/feature-tuning.html#when-to-use-fast-search-for-attribute-fields).

The connector uses the application's backend summary by default. `SummaryClass`
and `VectorSummaryClass` both default to `null`; choosing summary classes is optional.
`SummaryClass` applies to searches and filtered reads when `IncludeVectors` is false,
while `VectorSummaryClass` independently applies to `IncludeVectors = true`.
These options do not affect key-based document reads.

The sample schemas define a `vector_data` summary containing the data fields and
`documentid`, without the embedding. The console sample explicitly selects it with
`SummaryClass = "vector_data"`, since it owns those schema definitions. The generic
connector does not require that summary name.

`vector_data` is an explicit schema convention, not a guaranteed performance gain
over YQL projection. Vespa behavior depends on version: the
[implementation inspected at commit ea8a0ff](https://github.com/vespa-engine/vespa/blob/ea8a0ff1603577e1f40efa86e498ec73c75f6a48/container-search/src/main/java/com/yahoo/prelude/fastsearch/PartialSummaryHandler.java)
can request selected fields from the backend. When a projection asks for fields
outside the named summary, it can combine those fields with the summary's fields
and fall back to `[all]`. For example, selecting `embedding` with `vector_data`
can still return the vector; `select *` with that summary excludes it. Inspect
query traces and measure the deployed version instead of assuming that projection
always transfers a complete summary. The sample summary still needs disk-backed
content and document IDs; it is not an attribute-only summary.

When selecting a named summary, deploy it first. Leaving the options unset uses
the backend's default summary. Keep mapped data fields and `documentid` in the
summary, and the vector in the summary intended for vector reads, to avoid
conflicting requests and backend fallback. An incomplete or unknown summary is
not a reliable way to trigger client validation: validate deployment configuration
directly. The connector rejects requested vectors only when the actual response
contains an absent, null or invalid vector.
Unset optional data fields remain valid: a missing data field in an individual hit
does not establish that the summary is incomplete. Validate the summary definition
against the mapping during deployment instead of requiring every optional field
in every document. `EnsureCollectionExistsAsync` checks searchability, not this
complete schema contract.

For mandatory data, configure `RequiredDataFields` with **storage field names**.
Typed C# `required` data properties are also recognized automatically. A missing
or null required data field raises an error instead of returning a CLR default.
Optional nullable data remains valid; required-field validation is not a blanket
non-null check on every mapped property. The requested vector has its own mandatory
presence and validity checks regardless of this option.

## Dictionary records and generated embeddings

`GetDynamicCollection(name, definition)` returns a collection of
`Dictionary<string, object?>` records with `object` keys. The definition must give
each property's CLR type. Use its logical property names in dictionaries and
filters; `StorageName` maps them to Vespa fields.

```csharp
var definition = new VectorStoreCollectionDefinition
{
    Properties =
    [
        new VectorStoreKeyProperty("Key", typeof(Guid)),
        new VectorStoreDataProperty("UserId", typeof(string)) { IsIndexed = true },
        new VectorStoreDataProperty("Content", typeof(string)),
        new VectorStoreVectorProperty("ContentEmbedding", typeof(string), 768)
        {
            DistanceFunction = DistanceFunction.CosineSimilarity
        }
    ]
};
using var memories = store.GetDynamicCollection("chathistory", definition);
await memories.EnsureCollectionExistsAsync(ct);
await memories.UpsertAsync(new Dictionary<string, object?>
{
    ["Key"] = Guid.NewGuid(),
    ["UserId"] = "alice",
    ["Content"] = "Use C# for code examples.",
    ["ContentEmbedding"] = "Use C# for code examples."
}, ct);
await foreach (var hit in memories.SearchAsync("preferred programming language", 3,
    new() { Filter = r => (string?)r["UserId"] == "alice" }, ct))
    Console.WriteLine(hit.Record["Content"]);
```

This example requires a configured embedding generator and the deployed
`chathistory` schema. A string vector property supplies text to that generator;
Vespa stores the resulting dense tensor. Keep the original text in a data property
such as `Content`. With `IncludeVectors = true`, a string vector property is read
back as `ReadOnlyMemory<float>`. Precomputed `float[]` or `ReadOnlyMemory<float>`
values can also be supplied to that property and bypass generation.

Batch upserts enumerate bounded chunks and generate text vectors in bounded
embedding batches. A chunk's inputs and generated vectors are validated before
its writes; earlier chunks may already have been stored if a later chunk fails.
Mixed text/vector batches are supported. Caller dictionaries are not modified.
Unknown dictionary properties and values with incompatible types are rejected.
`Guid` keys are stored as canonical `D`-format document IDs and returned as `Guid`
values.

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

Start with the default search behavior: approximate search, backend summaries,
and Vespa's default graph exploration. The upsert limits (`64` records, `32` text
inputs per embedding request, `4` concurrent writes) bound client resource use;
they are configurable starting points rather than workload-optimal values.
Use the optional [benchmark](https://github.com/outmatic/Vespa.NET/blob/main/docs/vector-search-benchmarks.md)
when specific recall or latency requirements call for search tuning.

### Options reference

| `VespaVectorStoreOptions` | Meaning |
|---|---|
| `Namespace` | Required document namespace; dedicate the schema to it |
| `RankProfile` | Required deployed angular-proximity profile with distance match feature |
| `QueryTensorName` | Query input name; default `query_embedding` |
| `Approximate` | Use approximate nearest-neighbor search; default `true` |
| `HnswExploreAdditionalHits` | Optional nonnegative extra HNSW exploration; applies to approximate search. Default `null` leaves Vespa's default unchanged |
| `SummaryClass` | Optional deployed summary for searches/filtered reads without vectors; default `null` uses Vespa's default |
| `VectorSummaryClass` | Independent deployed summary for `IncludeVectors = true`; default `null` uses Vespa's default |
| `RequiredDataFields` | Optional storage names of mandatory data fields; typed C# `required` data properties are also enforced |
| `MaxResults` | Maximum `top + Skip`; default `400`. Vespa's query profile must also permit the requested hits and offset |
| `SearchTimeout` | Vespa per-query timeout; default five seconds. The HTTP client's timeout applies independently |
| `EmbeddingGenerator` | Optional caller-owned `IEmbeddingGenerator<string, Embedding<float>>` for text queries and dynamic string vector inputs |
| `DocumentEmbeddingPrefix` | Optional prefix added to dynamic string vector inputs before generation |
| `QueryEmbeddingPrefix` | Optional prefix added to text queries before generation |
| `UpsertBatchSize` | Maximum records buffered/validated per upsert chunk; default `64` |
| `EmbeddingBatchSize` | Maximum text inputs per embedding request; default `32` |
| `UpsertConcurrency` | Maximum concurrent document writes in a chunk; default `4` |

The caller owns the `IVespaClient` and embedding generator. Keep them alive for
all collections that use them and dispose them yourself; disposing a collection
or store does not dispose either dependency. Configure endpoints, authentication,
HTTP resilience, and telemetry through the base client.

Reuse the same `IVespaClient` for multiple stores or schema configurations. The
concrete `VespaVectorStore` also accepts complete options per collection:

```csharp
var defaults = new VespaVectorStoreOptions
{
    Namespace = "knowledge",
    RankProfile = "vector_search"
};
using var store = new VespaVectorStore(client, defaults);
using var articles = store.GetCollection<string, Article>("article", definition: null,
    options: defaults with { RequiredDataFields = ["category"] });
```

`GetDynamicCollection(name, definition, options)` provides the same override for
dictionary records. These overloads replace the options for that collection; use
`with` to retain the application defaults. Other collections and calls through
the standard `VectorStore` abstraction continue to use the store defaults. Required
field lists are copied when options are accepted. The caller still owns the client
and embedding generators, including any collection-specific generator.

Options are fixed when the store/collection is constructed. Changing a rank profile
or summary does not require another HTTP client or another Vespa deployment. The selected
profile/summary must already exist. HNSW construction settings and filter indexes
belong to the deployed schema, while `HnswExploreAdditionalHits` is a query-time
setting. Tune approximate-search recall against exact search before choosing it.

See [existing deployments, application configuration and Cloud](https://github.com/outmatic/Vespa.NET/blob/main/docs/agent-framework.md#reuse-an-existing-client-and-deployment)
for client reuse, DI, application packages and the distinction between Cloud data
access and Cloud deployment.

This provider implements only part of the VectorData surface. Schema deployment,
deletion and discovery, `CollectionExistsAsync`, other key or vector types, other distance metrics,
multiple vectors, deep export, and hybrid search interfaces are unsupported.
Use the [base Vespa.NET client](https://github.com/outmatic/Vespa.NET) for native
hybrid search, custom ranking, bulk feed, document visiting, and deployment.

## Real embeddings and Microsoft Agent Framework

The runnable
[Agent Framework sample](https://github.com/outmatic/Vespa.NET/tree/main/samples/Vespa.NET.AgentFramework)
uses OllamaSharp, `nomic-embed-text`, and a tool-capable chat model. Start Ollama
and the dedicated Docker instance above, then run from the repository root:

```sh
ollama pull nomic-embed-text
ollama pull qwen3:4b
dotnet run --project samples/Vespa.NET.AgentFramework -- --deploy --seed "How does vector search work?"
```

`--deploy` waits for the config server, uploads the included application package
with the client's Admin API, and waits for Vespa readiness before seeding or
querying. It replaces the active application on that instance; use a dedicated
development instance. `--deploy-only` deploys and exits without connecting to
Ollama. The application files are included in build and publish output, so the
published executable can deploy from any working directory.

`--seed` explicitly generates embeddings, writes three sample documents in the
`agent-framework` category, and waits until their IDs are searchable.
Omit `--deploy` and `--seed` on subsequent query-only runs. Set
`VESPA_CONFIG_ENDPOINT` when the config server uses a different address; by default
it uses the query endpoint's host with port `19071`. Configure `VESPA_ENDPOINT`,
`OLLAMA_ENDPOINT`, and `OLLAMA_MODEL` to change the other defaults (`http://localhost:8080`,
`http://localhost:11434`, and `qwen3:4b`).

The sample constructs an `OllamaApiClient`, uses it as
`IEmbeddingGenerator<string, Embedding<float>>`, and supplies it in store options.
Typed records supply precomputed document vectors. Dynamic collections can instead
declare a string vector property, which `UpsertAsync` embeds before writing.
With a generator configured, `SearchAsync(string, ...)` generates the query vector.
Vector inputs bypass that generator.
An explicitly configured generator must support
`IEmbeddingGenerator<string, Embedding<float>>`; incompatible generators are
rejected when the collection is created.

Use the same embedding model, dimension, and model-appropriate preprocessing for
feeding and querying. The sample follows Nomic's
[retrieval prefixes](https://huggingface.co/nomic-ai/nomic-embed-text-v1.5):
`search_document: ` for passages and `search_query: ` for queries. The connector
adds prefixes only when configured through `DocumentEmbeddingPrefix` and
`QueryEmbeddingPrefix`; both default to `null`. The native memory sample uses
these options, while the other sample modes prefix inputs themselves.
Do not mix the quickstart's artificial vectors with real
document embeddings; the quickstart removes its records on completion.

In [Program.cs](https://github.com/outmatic/Vespa.NET/blob/main/samples/Vespa.NET.AgentFramework/Program.cs),
`AIFunctionFactory.Create` wraps collection search as the `search_vespa` tool.
The function filters by `Published` and the sample category in application code and returns source
IDs with passages. `chat.AsAIAgent(..., tools: [search])` registers that function,
and `agent.RunAsync(...)` lets the model call it.
Retrieval code that needs only search can accept `IVectorSearchable<Article>`
instead of the concrete collection, leaving collection management to the caller.

The sample's `--memory` mode registers `remember_preference` to save a user's
preferred programming language for code examples and `recall_preferences` to
retrieve it. Records use the `memory` schema, a stable user/topic key for updates,
and direct key lookup for the user configured by the application. Recall verifies
the returned user/topic and does not generate a query embedding or poll search
visibility.
See the [sample README](https://github.com/outmatic/Vespa.NET/tree/main/samples/Vespa.NET.AgentFramework#remember-a-preference-across-runs)
for the commands and user configuration.

### Native chat-history memory

The sample's `--history-memory` mode uses Microsoft's unmodified
`ChatHistoryMemoryProvider` as an `AIContextProvider`. It stores message text and
metadata in the [`chathistory` schema](https://github.com/outmatic/Vespa.NET/blob/main/samples/Vespa.NET.AgentFramework/app/schemas/chathistory.sd),
using the provider's dictionary definition, `Guid` keys and string vector inputs.
The connector generates document embeddings and translates the provider's scope filters.
This is not a complete serialized archive of messages, tool calls or multimodal
content. The sample prints provider warning/error category, event ID and exception
type without conversation text or exception messages. Monitor these diagnostics:
the provider may log a storage failure while the model response still succeeds.

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- --deploy --history-memory "I prefer code examples in C#."
dotnet run --project samples/Vespa.NET.AgentFramework -- --history-memory "Which programming language should you use for my code examples?"
dotnet run --project samples/Vespa.NET.AgentFramework -- --history-memory --on-demand "What language do I prefer?"
```

Each run has a new session ID. Storage includes that session; search spans sessions
for the configured application, agent and user. The provider searches before model
invocation by default. `--on-demand` exposes its retrieval function to the model.
Messages are saved after the response; search visibility can lag behind document writes.
`VESPA_MEMORY_USER` selects the demo user, as in preference mode.

Compatibility is tested with Microsoft.Agents.AI 1.26.0:

| Agent Framework use | Support |
|---|---|
| Application-defined retrieval and preference tools | Supported |
| `TextSearchProvider` with a collection search delegate | Supported |
| `ChatHistoryMemoryProvider`, automatic and on-demand recall | Supported |
| `AgentSession` reuse and serialization alongside retrieval | Supported by Agent Framework; tested with this connector |
| Chronological conversation replay and complete session snapshots | Separate `Vespa.NET.AgentFramework` package |

Chat-history memory retrieves relevant messages semantically. Full session history
and session serialization remain Agent Framework responsibilities.
See the [conversation patterns guide](https://github.com/outmatic/Vespa.NET/blob/main/docs/agent-framework.md)
for chronological history, session snapshots, file memory and compaction backed by Vespa.

## Verify the connector

Use the [ANN benchmark guide](https://github.com/outmatic/Vespa.NET/blob/main/docs/vector-search-benchmarks.md)
to compare exact search with HNSW exploration settings, filter selectivity and
summary configurations on a dedicated deployment. It reports recall against exact
top-k and client-observed latency; use representative embeddings before tuning.

From the repository root:

```sh
dotnet test tests/Vespa.NET.VectorData.Tests -c Release
VESPA_VECTOR_DATA_INTEGRATION_TESTS=1 dotnet test tests/Vespa.NET.VectorData.Tests -c Release --filter FullyQualifiedName~LiveVespaTests
```

The default suite covers HTTP request bodies, mapping, filters, scores, errors,
cancellation, ownership, Agent Framework tool calls, and preference persistence,
replacement and user scopes. It also exercises Microsoft's native memory provider
in both retrieval modes, dynamic records and session serialization.
Chat and embedding tests use deterministic doubles.

The opt-in Docker tests create their own Vespa containers. One exercises the
connector, retrieval tool and preference memory across fresh agents against
three-dimensional schemas. The other runs the published sample with
`--deploy-only`, then executes the C# quickstart against its application.
Additional Docker tests verify native chat-history memory and schema checks against
real Vespa. These tests are skipped unless enabled.
Set `VESPA_VECTOR_DATA_IMAGE` to select the test image.
