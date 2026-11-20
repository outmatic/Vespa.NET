# VectorData connector development

See the [package README](../src/Vespa.NET.VectorData/README.md) for installation,
the runnable quickstart, schema requirements, options and Agent Framework usage.
That README is also the documentation embedded in the NuGet package.

This guide and the package README describe version `2.0.0`, including the explicit
initialization contract. Applications upgrading from `1.2.0` must supply an
authoritative collection catalog or set `AssumeSchemaExists = true` when their
deployment process guarantees the schema already exists.

## Release process

The CI workflow builds and tests pull requests, runs the connector's live Vespa
tests, and verifies its packed artifact against published dependencies on .NET 8
and .NET 10. After merge, a push to `main` that increases the connector's project
version publishes that verified artifact to NuGet once all checks pass. Unchanged
versions do not trigger automatic publication. Manual publishing remains available
through the CI workflow dispatch input for recovery.

## Package boundary

`Vespa.NET.VectorData` is an optional package using only the public `IVespaClient`
API and standalone `Microsoft.Extensions.VectorData.Abstractions`. The base client
and models acquire no connector dependencies. Agent Framework and OllamaSharp
remain consumers in the sample, and Agent Framework is used in integration tests.

The separate `Vespa.NET.AgentFramework` package implements chronological history,
session persistence and file memory. Its Microsoft dependency does not flow into
VectorData or the base client. See the [conversation patterns guide](agent-framework.md)
for the pattern map and caveats.

The package has its own version, independent of the base
client's `3.1.1`. Version changes describe compatibility of the connector API;
they do not mirror SDK or Microsoft dependency versions. Keeping its project and tests here
allows changes to be checked against the client in one build. It can move to a
separate repository when independent maintainers or release scheduling justify
that overhead: replace its client project reference with a NuGet dependency and
move its tests, sample and documentation. No internal client APIs are required.

## Implementation and capability boundaries

The provider targets `Microsoft.Extensions.VectorData.Abstractions` 10.11.0.
Its supported records and filters are provider-specific; adding a capability
interface does not imply that every VectorData mapping or optional pattern works.
Microsoft's [VectorData guide](https://learn.microsoft.com/en-us/dotnet/ai/vector-stores/how-to/use-vector-stores)
describes the shared abstraction, create-if-missing expectation and separate hybrid
capability; the provider-specific boundaries below take precedence for this connector.

| Surface | Implementation / boundary |
|---|---|
| Core key CRUD, batch operations | Typed/dynamic records; caller-assigned string, Guid, int, long or ulong identity; bounded upsert preparation/concurrency, explicit partial outcomes |
| Core filtered retrieval | Comparisons, safe promotions, string/integer membership, limited array/list `Any`, ordered indexed scalar fields and bounded paging |
| Core dense search | Zero-vector collections reject search; one vector is inferred; multiple vectors require selection and explicit query/rank bindings |
| Core existence | Store and collection `CollectionExistsAsync` require an authoritative application catalog; no inference from search results |
| Core listing | `ListCollectionNamesAsync` delegates to optional `IVespaCollectionCatalog`; unsupported without it |
| Core ensure/create/delete | Ensure requires a catalog or explicit predeployment assertion, then checks searchability on each call. No creation/deletion/deployment |
| Concrete searchability helper | `ValidateSearchabilityAsync` checks query execution; it does not certify schema existence |
| Extra hybrid capability | `IKeywordHybridSearchable<TRecord>` retrieves dense OR lexical candidates, applies filters to both, and returns deployed combined relevance |
| Not implemented | Automatic keys; DataContent embedding input; sparse/binary/non-float vectors; other metrics/index kinds; nested/immutable constructor-only mappings; validated trimming/Native AOT support |

The catalog must describe the deployed application, include empty schemas and
separate absence from operational failure. Both existence and listing throw
without a catalog. A successful data-plane query can involve aliases/routing and
is not proof of schema existence; no search-result inference is used.

Ensure uses the catalog first when configured, then validates searchability.
Without a catalog it requires `AssumeSchemaExists = true` (default false), an explicit
application assertion that deployment has already happened. This assertion enables
neither discovery nor authoritative existence checks. A catalog can still reject
an absent schema even when the assertion is set. Ensure never creates a schema
and rechecks on each call. The concrete collection's public
`ValidateSearchabilityAsync` performs just the read-only execution check.

Neither binding a model nor initialization validates the deployed field, summary,
tensor, index or rank-profile contract. Catalogs are application-supplied and
read-only: they do not activate Admin APIs or reconcile schemas.

Typed and dictionary records share an immutable mapping model. They can contain
zero, one or several dense vector fields. Supported vector values are `float[]`,
`ReadOnlyMemory<float>` (including nullable memory), `Embedding<float>` and generated
string inputs. Nullable typed vectors can be absent; nonnullable requested vectors
must be present and valid. Typed string inputs are not reconstructed from stored
tensors: their properties read as null, while dynamic string-vector definitions
return `ReadOnlyMemory<float>` when requested. Store original text separately.
Generator precedence is property, definition, store; generators remain caller-owned.

Scalar data includes dates and one-dimensional arrays/`List<T>`. DateTime requires
UTC; DateTimeOffset normalizes to UTC. Both use signed Unix-epoch ticks (100 ns) in
Vespa long fields. DateOnly uses signed days since 1970-01-01. The shared codec is
used for both document mapping and comparisons. Null/missing predicates, empty-string
terms, arbitrary expression execution and general collection predicates remain
unsupported. Numeric membership uses YQL `in` (Vespa 8.293.15+); string membership
uses exact cased annotated terms. Full-text equality is not substituted for .NET
string equality.

The six metrics are cosine similarity/distance, dot-product/negative-dot-product,
and Euclidean/squared-Euclidean distance. The README documents each score conversion
and threshold direction. Distance thresholds are upper bounds; similarity thresholds
are lower bounds. Dense results must remain ordered by increasing backend distance.

Filtered retrieval and searchability probes explicitly use `unranked`; the
application's default ranking is not needed for these operations. Dense dot-product
thresholds are also pushed into `distanceThreshold` with an outward-rounded inclusive
bound and a final client check. Angular and Euclidean thresholds remain client-side
because their distance features are reconstructed through closeness: the backend's
raw-distance cutoff is not numerically interchangeable at every boundary. The live
suite includes a nonzero Euclidean point whose public distance rounds to zero.
Hybrid thresholds remain combined-score filters and are not pushed into either
retrieval branch.
Hybrid scores are the separately configured profile's combined relevance, not dense
scores. `VectorFields` binds each selected vector to its query tensor and the
profile required by that operation. `RankProfile` is optional for CRUD; dense
search requires a default profile for a single vector or an explicit per-field
profile for multiple vectors. Hybrid requires its own global/per-field
`HybridRankProfile`, not a dense profile. `HybridVectorTargetHits` and
`HybridKeywordTargetHits` independently set the dense and lexical candidate targets.
Each defaults to `top + Skip`, which is also its minimum effective value. These are
result-window defaults, not a measured recall or latency optimum: tune both on
representative queries and coordinate them with the deployed reranking budget.
See Vespa's [candidate target contract](https://docs.vespa.ai/en/reference/querying/yql.html#totaltargethits)
and [phased ranking](https://docs.vespa.ai/en/ranking/phased-ranking.html).
`IndexKind.Flat` forces exact queries and suppresses HNSW exploration even if
`Approximate` is true. Hnsw/unspecified metadata follows the Approximate setting.
None of these options provisions or inspects server indexes.

Summary selection is optional: both settings default to null. Named summaries
must already exist; current Vespa may merge projected fields through backend
fallback, so benchmark the deployed version rather than assuming a performance
benefit. Required data uses `RequiredDataFields` or C# `required`; optional unset
fields remain valid. Search verifies document identity but does not provide
server-side tenant isolation: dedicate each schema to its configured namespace.

Writes are document PUTs, not transactions or bulk-feed calls. Ignored fields can
mean that other fields were already stored. Upsert failures expose consumed-input
outcomes and whether enumeration finished; attempted failures remain unconfirmed.
See the [batch contract](../src/Vespa.NET.VectorData/README.md#bounded-writes-and-partial-outcomes).
Read/backend failures use contextual VectorStoreException; caller cancellation
remains cancellation. Bounded result windows do not provide deep export; document
visiting is available through the base client, not a missing mandatory VectorData API.

## Agent Framework patterns

Automatic and on-demand `ChatHistoryMemoryProvider` use dynamic Guid records,
string embedding input and scope filters. Retrieval tools and `TextSearchProvider`
use collection search delegates. These patterns do not require schema deployment,
all possible VectorData mappings or the extra hybrid interface. Session reuse and
serialization remain framework behavior; chronological replay, complete session
snapshots and file memory use `Vespa.NET.AgentFramework`. The shared storage and
consistency limitations still apply. See the [pattern guide](agent-framework.md).

## Verify changes

The [ANN benchmark](vector-search-benchmarks.md) measures exact/approximate
agreement and latency with configurable filters, summaries and datasets. It is a
separate workload from correctness tests and does not establish universal HNSW defaults.

From the repository root:

```sh
dotnet build Vespa.NET.slnx -c Release
dotnet test tests/Vespa.NET.VectorData.Tests -c Release
VESPA_VECTOR_DATA_INTEGRATION_TESTS=1 dotnet test tests/Vespa.NET.VectorData.Tests -c Release --filter 'FullyQualifiedName~LiveVespaTests|FullyQualifiedName~LiveFeatureTests'
dotnet pack src/Vespa.NET.VectorData -c Release
```

The default tests read actual HTTP request bodies and cover mapping, filters,
numeric conversions, errors, lifetime, cancellation and an Agent Framework tool
loop with deterministic model/embedding doubles. Native memory tests use Microsoft's
unmodified `ChatHistoryMemoryProvider` in automatic and on-demand modes, plus
`TextSearchProvider` alongside session reuse and serialization. Docker tests are reported skipped
until enabled. Each creates and disposes a dedicated Vespa container; they never
deploy to another running application. Set `VESPA_VECTOR_DATA_IMAGE` to pin an image.

One Docker test checks CRUD, search, score conversion, filtering and Agent Framework,
including preference persistence and recall with fresh agents, updates and user filters.
The other publishes the Agent Framework sample, runs its `--deploy-only` command,
then extracts the README's complete C# example, builds it in a temporary consumer
project, executes it against the deployed 768-dimensional schema, and verifies
document cleanup. Running this test requires the .NET 10 SDK as well
as Docker. The test application's relaxed disk threshold is not applied to the
sample application distributed to users.

Additional Docker tests exercise schema checks (including empty and undeployed
schemas) and Microsoft's native memory provider: message persistence, automatic
recall, on-demand retrieval, case-sensitive user scopes and `Guid` CRUD.
`LiveFeatureTests` exercises expanded mappings, metric scores/thresholds, membership,
ordering and hybrid lexical-only candidates with scope filtering. These describe
coverage scenarios, not a guarantee that the current checkout has passed a run;
record unit and enabled-live results separately when reviewing changes.

The [Agent Framework sample](../samples/Vespa.NET.AgentFramework/README.md) supports
`--deploy --seed` for its first run and `--deploy-only` to prepare a dedicated
development instance without Ollama. Deployment uses the client's existing Admin
API with the checked-in application package; collection lifecycle remains outside
the VectorData provider.

The sample's `--memory` mode registers application-defined tools for saving and
recalling one programming-language preference per user. A stable user/topic key
replaces an earlier preference. Recall reads that key, checks its user/topic metadata
and needs no query embedding or search-visibility polling. Run the two
commands in the sample README as separate processes to demonstrate persistence
without replaying the previous conversation. `VESPA_MEMORY_USER` is a demo scope,
not authentication or a tenant boundary.

The `--history-memory` mode uses `ChatHistoryMemoryProvider` with the checked-in
`chathistory` schema. The provider supplies dictionary records and scope filters;
the connector handles mapping, `Guid` keys and string-to-vector generation. Its
document/query prefix options let the sample apply Nomic preprocessing without
changing the provider. Storage records each session while search spans the
configured user's sessions. This is semantic recall, not chronological replay.

## Prepare a NuGet package

Pack only the connector; its `Vespa.NET >= 3.1.1` dependency is available on nuget.org:

```sh
dotnet pack src/Vespa.NET.VectorData -c Release -o artifacts/packages
python3 scripts/verify-vector-data-package.py artifacts/packages
```

The verifier checks the package metadata, both target frameworks and the embedded
README. It then runs the existing non-Docker tests against the actual package on
.NET 8 and .NET 10, restoring the client from nuget.org into an isolated cache.
It requires Python 3, both .NET runtimes, the .NET 10 SDK and network access.

The manual **Package VectorData** workflow (`package-vectordata.yml`) performs
these checks and uploads only the connector's `.nupkg` as a downloadable artifact.
It contains no NuGet publication step and is not triggered by a push or tag.
The existing `CI` workflow still builds/tests on a push to `main`; its `v*` tags
publish the three base SDK packages, so do not create such a tag for the connector.

## Publish the connector

The **CI** workflow (`ci.yml`) also supports a manual run. It builds and tests the
solution, packs only the connector, verifies the actual package against the
published client on .NET 8 and .NET 10, and uploads it as an artifact:

```sh
gh workflow run ci.yml --ref main -f publish_vector_data=false
```

After reviewing the release changes, publish with:

```sh
gh workflow run ci.yml --ref main -f publish_vector_data=true
```

Publishing is allowed only from `main`, after both build/test and package
verification jobs pass. The publish job downloads the verified artifact and
pushes that same package to nuget.org without rebuilding it. It uses the existing
`ci.yml` trusted-publishing policy and the organization `NUGET_USER` secret; the
NuGet policy must allow a new `Vespa.NET.VectorData` package, not only new versions
of the base SDK packages. No publication occurs on a normal push, pull request,
or a manual run with `publish_vector_data=false`.

Use `connector-v<version>` if tagging a reviewed connector release. Do not use a tag starting
with lowercase `v`: the existing `v*` trigger publishes the three base SDK packages.
