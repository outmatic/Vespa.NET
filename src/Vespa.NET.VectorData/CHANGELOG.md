# Vespa.NET.VectorData changelog

This package is versioned independently of the base Vespa.NET SDK.

## 2.0.0

### Breaking changes

- `EnsureCollectionExistsAsync` now requires an authoritative collection catalog
  or the explicit application assertion `AssumeSchemaExists = true`. It no longer
  treats a successful search as proof that a schema exists. Existing applications
  that manage deployment separately must set this option or provide a catalog.

### Features and fixes

- Filtered reads and searchability probes use `unranked` explicitly, avoiding
  unnecessary application-default ranking.
- Dense dot-product and negative-dot-product score thresholds also filter on the
  server with conservative inclusive bounds. Angular/Euclidean thresholds retain
  client filtering to preserve floating-point boundary semantics; hybrid thresholds
  continue to apply to combined relevance.

- Require an authoritative `IVespaCollectionCatalog` for store/collection existence
  checks and listing; successful data-plane queries are not treated as schema
  existence proof. No automatic schema deployment or deletion is implemented.
- Make ensure explicitly check-only: use the catalog, or opt in with
  `AssumeSchemaExists = true` to assert application-managed predeployment, then
  validate searchability. Add concrete `ValidateSearchabilityAsync` for that query
  check alone. The assertion does not enable discovery or validate schema identity.
- Support vectorless CRUD and multiple selected vector fields, with explicit
  per-field rank-profile/query-tensor bindings through `VectorFields`. RankProfile
  is optional for CRUD, required by dense search; hybrid uses its own profile.
  `IndexKind.Flat` forces exact queries and disables HNSW exploration.
- Add caller-assigned int, long and ulong keys; dense `Embedding<float>` values,
  nullable typed vectors, typed string embedding inputs and per-property generators.
  Typed input strings are not read back from stored tensors; store source text separately.
- Add scalar `List<T>` and date mapping. UTC DateTime/DateTimeOffset use 100 ns ticks
  since Unix epoch; DateOnly uses epoch days. Date comparisons use the same codec.
- Support cosine similarity/distance, dot-product/negative-dot-product and
  Euclidean/squared-Euclidean scores with metric-appropriate threshold direction.
- Implement the optional `IKeywordHybridSearchable<TRecord>` interface: nearest-neighbor
  OR weakAnd candidates, filters on both branches and combined relevance from an
  explicit hybrid profile. Add `HybridRankProfile` and independent
  `HybridVectorTargetHits` / `HybridKeywordTargetHits` settings. Each candidate target
  defaults to and is bounded below by `top + Skip`; tune them with the deployed
  reranking budget rather than treating the defaults as a measured optimum.
- Add scalar set membership, array/list Contains, limited Any membership and ordered
  filtered retrieval. Recognize C# 14 array-to-span Contains expressions without
  executing them. Numeric set queries require Vespa 8.293.15 or later.
- Preserve explicit rejection of unsupported null/empty-string predicates, unsafe
  casts, full-text equality and arbitrary expression execution. No schema/index
  auto-validation or trimming/Native AOT contract is introduced.
- Add unit and opt-in live scenarios for the expanded surface; release verification
  remains separate from the presence of these tests.

## 1.2.0

- Collection-specific options let one store/client bind schemas with different
  ranking, summary and required-field contracts without changing store defaults.
- Requested vectors are assigned once, without first invoking setters with an
  empty/null value; reads support setters that reject null vectors.
- Optional `SummaryClass` and `VectorSummaryClass` independently select summaries
  without and with vectors. Both default to `null`, using the application's backend
  summary. The sample explicitly selects its `vector_data` summary; performance
  relative to YQL projection is version-dependent.
- Requested vectors actually absent, null or invalid in a response fail explicitly. Optional
  unset data fields remain supported; deployment must validate the summary contract.
- `RequiredDataFields` and typed C# `required` data properties enforce mandatory
  data presence without rejecting legitimate optional fields.
- Key-based reads use document field sets to avoid fetching unrequested vectors.
- Bounded upsert chunks, embedding batches and write concurrency limit buffering
  and expose partial failure instead of implying transactional writes.
- `VespaVectorUpsertException` and cancellation-compatible
  `VespaVectorUpsertCanceledException` expose per-input outcomes and whether input
  enumeration completed; attempted failures remain explicitly unconfirmed.
- Optional `HnswExploreAdditionalHits` tunes approximate query exploration.
- Sample schemas include summaries without embeddings and indexed scope filters.
- Configuration guidance covers existing client/deployment reuse, application
  package options, Cloud access and deployment, and Vespa consistency limits.
- The preference sample reads its stable key without semantic search or query
  embeddings; native-memory failures have visible diagnostics without content logging.

## 1.1.0

- Dynamic dictionary collections with explicit property definitions, storage names
  and indexed scalar filters.
- Caller-assigned `Guid` keys in typed and dynamic collections.
- Embedding generation for dynamic string vector properties, including batch
  generation, mixed precomputed/text inputs and validation before document writes.
- Optional document and query embedding prefixes for models such as Nomic.
- Read-only `EnsureCollectionExistsAsync` validation of an already deployed schema.
  Application deployment remains external to the connector.
- Microsoft Agent Framework's unmodified `ChatHistoryMemoryProvider`, with automatic
  and on-demand recall, scope filters and persistence across sessions. The sample
  includes a complete schema and `--history-memory` mode.
- Tests for native memory, `TextSearchProvider` and session serialization, including
  native memory against a real Vespa instance.

## 1.0.0

- Typed `Microsoft.Extensions.VectorData` collections over existing Vespa schemas,
  with caller-assigned string keys and one dense float vector per record.
- Record upsert, key-based reads and deletes, filtered retrieval, and vector
  search with cosine scores, paging and scalar filters.
- Text queries through a caller-owned `IEmbeddingGenerator`; applications generate
  document embeddings before writing records.
- Explicit failures for unsupported schema lifecycle operations, mappings and
  filters, with contextual errors for backend failures.
- Microsoft Agent Framework examples for documentation retrieval and a user
  preference remembered across separate runs, with Ollama for chat and embeddings.
- .NET 8 and .NET 10 targets, depending on Vespa.NET 3.1.1 or later. Agent Framework
  remains a sample dependency, and the connector adds no embedding-provider dependency.

See the [package README](README.md) for schema requirements, supported operations
and limitations.
