# Changelog

All notable changes to this project are documented in this file.

## [Unreleased]

### What's new in Vespa.NET 4.0

Conditional writes now accept C# predicates such as `condition: p => p.Price > 0`. Pass existing Vespa selectors through `rawCondition:`. If you're upgrading from 3.x, follow the migration guide and rebuild callers of the changed APIs. [Migration guide](docs/migration-4.0.md)

The client targets .NET 8 and .NET 10. The core client and VectorData use Vespa 8.640.27 as their server baseline.

#### Document and feed APIs

- Use the same predicate style for visits, streaming search selections and updates, deletes or copies by selection. Pass existing Vespa selections through `rawSelection:`.
- Request fields with `DocumentFieldSet` and pass read or write options alongside the operation. Model-aware document extensions went from 37 methods in 3.1.1 to 30 (**19% fewer**). Of those, read methods went from 10 to 5 (**50% fewer**).
- Use the same condition pattern for mixed feed operations and homogeneous batches. `DocumentCondition.FormatLiteral` formats values for native Vespa selectors, including decimals on hosts that use a comma as the decimal separator. [Document operations](docs/document-operations.md) · [Feed](docs/feed.md)

#### Search, deployment and adapters

- Derive search requests and ranking configurations with `with`. Fluent request helpers return a new request, so pagination and ranking overrides can share a base request. [Migration guide](docs/migration-4.0.md#search-requests-and-ranking)
- Choose sort direction, nearest-neighbor execution mode and tensor update operations with enums. Use `YqlTermAnnotations` to set term options by name. Grouping supports continuation with an inline expression or the request's `Select` property. [Search](docs/search.md) · [Grouping](docs/grouping.md)
- Generated application packages report conflicting schema struct names before deployment. Set the minimum Vespa version for a package when it uses features that need a newer server. [Schema generation](docs/schema.md)
- Use `WithFieldMaps()` when sharing a client between VectorData and Agent Framework. Typed VectorData records can use `BindCollection` in trimmed applications. Agent Framework history preserves numeric tool results with JSON reflection disabled. Both adapters keep independent package versions. [VectorData](src/Vespa.NET.VectorData/README.md) · [Agent Framework](src/Vespa.NET.AgentFramework/README.md)

#### Trimming and Native AOT

Register source-generated JSON metadata for the documents, projections and grouping summaries your client reads or writes. Use the optional ID callback to populate IDs on reads. Register document and struct types with `VespaSchemaMetadata` to generate an application package in a trimmed or Native AOT app. Tensor properties need the Vespa tensor converter in the registered JSON contract. [Trimming and Native AOT](docs/configuration.md#trimming-and-native-aot)

An installed-package smoke test exercised the registered paths in JIT, trimmed and Native AOT builds on .NET 8 and .NET 10, on macOS Arm64 and Linux x64. It covered document operations, feed, grouping, schema packages, VectorData search and Agent Framework persistence. The Linux x64 runs used emulation. [Compatibility notes](docs/configuration.md#trimming-and-native-aot)

The performance comparison used the same earlier 4.0 candidate packages and generated metadata in both self-contained JIT and Native AOT builds on an Apple M5. The client issued one request at a time against an in-memory HTTP handler. The first-response measurements include process startup. The steady-state measurements follow warm-up, using a 322-byte GET response or a search response with 25 hits.

| Runtime and workload | First response, p50 | Warm request latency, p50 | Warm requests/s |
| --- | ---: | ---: | ---: |
| .NET 8 GET | 109.09 → 13.47 ms (**−88%**) | 3.12 → 4.04 µs (**+29%**) | 288,783 → 231,973 (**−20%**) |
| .NET 8 search | 156.72 → 13.89 ms (**−91%**) | 74.25 → 91.71 µs (**+24%**) | 12,448 → 10,344 (**−17%**) |
| .NET 10 GET | 79.90 → 12.05 ms (**−85%**) | 10.62 → 3.62 µs (**−66%**) | 90,784 → 265,565 (**+193%**) |
| .NET 10 search | 104.18 → 12.44 ms (**−88%**) | 170.58 → 83.29 µs (**−51%**) | 6,290 → 11,363 (**+81%**) |

On .NET 10, resident memory after the first search fell from 59.3 to 21.0 MiB (**−65%**). Startup improved on both runtimes. Warm throughput rose on .NET 10 and fell on .NET 8 in this fixture. Network and Vespa server time are outside these measurements.

#### Client response processing

These measurements compare the 4.0 candidate with the earlier 4.0 build at `0693003`. They use .NET 10.0.10 on Arm64 and in-memory HTTP responses.

| Workload | Allocated bytes per response | Client time per response |
| --- | ---: | ---: |
| Grouping with 24 groups and four aggregation values per group | 48,593 → 42,449 B (**−12.6%**) | 45.44 → 43.47 µs (**−4.3%**) |
| Search with 50 hits and feature maps at both hit and field level | 113,979 → 83,544 B (**−26.7%**) | 179.24 → 160.97 µs (**−10.2%**) |

The search response with feature maps only inside `fields` allocated 71,224 bytes on both builds. These figures measure client allocations for the scenarios shown.

#### Feed loop costs

BenchmarkDotNet measured operation construction on .NET 10.0.10 Arm64. Pass a pre-rendered condition when the same predicate applies to every operation. With a varying threshold, use `FormatLiteral` to format the value for a native condition. These savings come from how you construct operations in your application. [Feed conditions](docs/feed.md#conditions)

| Condition | Construction time | Allocated bytes per operation |
| --- | ---: | ---: |
| Render the same predicate each time → reuse its rendered string | 1,063 → 8.7 ns (**−99.2%**) | 2,104 → 72 B (**−96.6%**) |
| Render a predicate with a captured threshold → format just the threshold | 894 → 52.4 ns (**−94.1%**) | 1,806 → 206 B (**−88.6%**) |

## [3.1.1] - 2026-11-09

### Changed

- Repeated `AddVespaClient` calls now replace the previous configuration instead
  of throwing for an unnamed client or combining named configurations. The last
  call supplies the options, HTTP callback and resilience strategy without
  stacking handlers or retry policies. Switching to or from
  `AddVespaClientWithResilienceStrategy` also replaces the unnamed configuration.

## [3.1.0] - 2026-10-17

### Added

- `GroupingSearchResponse<T>.AsTyped()` with typed summaries and nested groups
  through `TypedGroupingSearchResponse<T>`, `VespaGroup<T>` and `VespaGroupList<T>`.
  Existing grouping signatures and models remain available.
- `GetAggregation<TValue>` and `TryGetAggregation<TValue>` on both grouping models,
  reading original JSON for exact integers, decimals and structured outputs.
- Mixed `FeedAsync` overloads for synchronous and asynchronous sources of
  `FeedOperation.Put`, `Update` and `Delete`, with model-aware or explicit targets,
  conditions, upserts and per-operation request options.
- `FeedOptions` for concurrency, bounded capacity and progress. Mixed feeds preserve
  source order per document across retries, including equivalent full/bare IDs,
  while processing independent documents concurrently.
- `FeedResult.TotalOperations` and mixed-feed identity metadata on `FeedError`
  and `FeedProgress`, including a zero-based source operation index.
- Model-aware document overloads accepting `DocumentRequestOptions`, including
  field-update builders, group/number reads and updates, and `GetManyAsync`.
  Existing method signatures and positional cancellation-token calls are retained.
- Model-aware `PutByGroupAsync`, `PutByNumberAsync`, `DeleteByGroupAsync` and
  `DeleteByNumberAsync` with request options.
- `VisitPageAsync<T>` and `VisitAllPageAsync<T>` for one-request visit pages and
  persisted continuation checkpoints. `VespaVisitResponse<T>.IsComplete`
  distinguishes completion from an empty page with further work.

### Fixed

- JSONL visits retain full document IDs, log informational/warning messages and
  reject truncated streams or incomplete continuation markers.
- Selection operations surface malformed, truncated or server-error responses
  instead of silently returning a zero document count.
- Tensor deserialization uses `[VespaTensor]` metadata or an explicitly configured
  converter type to decode value-only formats and mapped labels named `type`,
  `values`, `cells` or `blocks`. Hex values without a known tensor type now throw
  `JsonException` instead of guessing a cell width.
- Grouping responses retain root-level outputs and summaries, rest buckets,
  coverage, backend errors and query trace.
- Search HTTP failures preserve the primary `root.errors` entry in
  `VespaException.Error`.
- Updated SourceLink build tooling and Testcontainers to resolve NuGet vulnerability
  warnings from `Microsoft.Build.Tasks.Git` and `SSH.NET`, allowing standard builds
  with NuGet auditing enabled.

## [3.0.1] - 2026-07-17

### Fixed

- **`SearchHit<T>.MatchFeatures`/`SummaryFeatures` were always `null` with
  Vespa's default JSON renderer.** The default renderer emits
  `matchfeatures`/`summaryfeatures` as pseudo-fields *inside* `fields`, while
  the hit model only read them at hit level. A dedicated `SearchHit<T>`
  converter now lifts them out of `fields`, so `GetMatchFeature(...)`/
  `GetSummaryFeature(...)` work against real server responses.
  - Hit-level occurrences (custom renderers) are still read; `fields` wins
    when both are present.
  - The `fields` object is passed to `T` unchanged, so models that declare
    `matchfeatures` themselves (pre-3.0.1 workarounds) keep working.

## [3.0.0] - 2026-07-09

Correctness-focused major release: every generated artifact (YQL, grouping
expressions, document JSON, tensor JSON, `.sd` schemas, application packages)
was re-verified against the official Vespa references and, where the docs were
ambiguous, against the Vespa server sources. The entire surface is now also
exercised end-to-end against a real Vespa container (154 integration tests).

### Breaking changes

- **Selection operations** (`UpdateWhereAsync`/`DeleteWhereAsync`/`CopyWhereAsync`):
  the `Stream` option was removed — Vespa never streams JSONL for these
  operations, so the old path reported `DocumentCount = 0` and crashed on
  multi-chunk continuations. Responses are now parsed per the actual server
  format; multi-chunk operations complete and report correct counts. A
  livelock guard aborts (with guidance) when the server keeps returning the
  same continuation with zero documents for a too-small `TimeChunk`.
- **`VespaResponse.IgnoredFields` / `SelectionPageResult.IgnoredFields`**
  (string list) replaced by `HadIgnoredFields` (bool) — the
  `X-Vespa-Ignored-Fields` header is a flag, not a field list.
- **Visiting** consolidated onto a `VisitOptions` record (replacing 14-parameter
  signatures). JSONL visits now auto-resume from trailing continuation tokens,
  surface in-stream `message` errors, and validate the `application/jsonl`
  Content-Type (Vespa ≥ 8.593). `DryRun` is structurally unavailable on visits
  (the server rejects it); `WantedDocumentCount` is not sent on JSONL visits.
- **Feed**: `FeedResult` is now immutable; faulted pipelines throw
  `VespaFeedException` carrying the partial `FeedResult` instead of discarding
  progress; `FeedProgress` gained `ProcessedCount` and is monotonic.
- **Document ids**: full ids are parsed structurally — `g=`/`n=` location
  selectors route to `/group/…`/`/number/…` paths (previously they silently
  addressed a *different* document), and a namespace/doctype mismatch with the
  operation target throws. Deserialized documents and hits retain the full id
  (`FullId`); the destructive `VespaIdConverter` was removed.
- **`VespaApplicationStatus`** now models the real config-server response
  (`Generation`); the previous properties never contained data.
- **Grouping DSL**: `WhereTrue()` removed (no longer in the grouping grammar —
  use `Filter(...)`/`Keep(...)`); `MathFloor`/`MathAbs`/`Cmp` removed (those
  functions do not exist in the grouping language); static `Each()` removed
  (grouping requests must be rooted at `all(...)`).
- **Search**: the no-op `namespace` parameter was removed from
  `NearestNeighborSearchAsync`; `streaming.userid` removed (no longer in the
  query API); `RankingConfig.RerankCount` now serializes to
  `ranking.secondPhase.rerankCount`.
- **`YqlBuilder.Build()`/`ToString()`** now emit `limit` as
  `limit + offset` per YQL semantics (YQL `limit` caps the total result index,
  not the page size), so the raw-YQL and `ToSearchRequest()` paths return the
  same page.
- **Client construction**: certificate options on a caller-supplied
  `HttpClient` now throw (previously the certificate was silently not used);
  `ApiKey` conflicting with a pre-set `Authorization` header throws; endpoints
  must be absolute http/https URLs; duplicate unnamed `AddVespaClient`
  registrations throw. `EnableRetry`/`EnableCircuitBreaker` are documented as
  DI-only (plus feed-level retry) and log a warning when unenforceable.
- **Schema generation** now fails fast at generation time for shapes the
  config server rejects at deploy: enum/`byte`/`ulong`/`DateTime` properties,
  predicate fields without arity, attribute mode on single-struct fields,
  recursive structs, `Redundancy > NodeCount`, colliding additional files.
- `VespaException` constructors consolidated; `DistanceMetric.InnerProduct`
  and the `Functions` rank-profile string DSL are obsolete
  (use `PrenormalizedAngular` and `[VespaRankFunction]`).

### Fixed

- **YQL generation**: `geoBoundingBox` now uses the documented 5-argument
  positional form (the previous output could never parse); `predicate()` emits
  `0` instead of the unparseable empty map `{}`; `ulong` values beyond
  `long.MaxValue` and non-formattable values throw instead of emitting invalid
  YQL; grouping continuations are spliced quote-aware (no longer corrupt
  queries containing `|` inside string literals).
- **Tensor JSON**: nested dense arrays (`[[…],[…]]`, the `short-value` form)
  parse correctly instead of crashing or silently truncating; mixed-tensor
  block arrays parse as blocks instead of zero-valued cells; the documented
  hex string forms are supported on read; empty `cells` arrays parse as mapped
  tensors; dictionary tensors with non-standard numeric element types
  serialize via conversion instead of writing `{}` (unrepresentable types
  throw); typed getters convert numerically instead of returning `null`.
- **Schema generation**: `schema X inherits Y` now also emits
  `document X inherits Y` (required by Vespa); garbage-collection `Selection`
  moved from the (invalid) `.sd` document block to services.xml; map and
  array-of-struct fields emit the required `struct-field` blocks; zero-argument
  rank functions emit `name()` (the config server rejects the bare form);
  nested structs are collected recursively; predicate fields emit the mandatory
  `index { arity }`; multi-node packages generate `hosts.xml`; tensor query
  `inputs` are emitted into every non-inheriting rank profile; implicit-double
  tensor type specs (`tensor(x{})`) parse.
- **HTTP/error handling**: error bodies are read once and parsed as structured
  `VespaError` across Documents, Search and Admin (previously non-JSON error
  bodies could throw `InvalidOperationException`, and Search/Admin lost the
  structured error); GETs and visits surface the server error message.
- `VespaSearchRequest.ShallowClone()` deep-copies `CustomParameters`/`Input`;
  `Coverage`, `VespaError` and hits no longer drop documented response fields.
- `MatchMode.Cased`/`Uncased` and `In(...)` documentation corrected (the docs
  previously described the opposite behavior).

### Added

- **YQL DSL**: negation (`Not(...)`, near/onear exclusions, sameElement
  terms), `Raw(...)` escape hatch, full term annotations (`weight`, `prefix`,
  `suffix`, `substring`, `significance`, `ranked`, `language`, `nfkc`,
  `implicitTransforms`), `Text(value, language)`, sameElement `elementFilter`,
  parameter-substituted `in (@param)`, `totalTargetHits`/`minTargetHits` on
  nearestNeighbor/weakAnd/wand.
- **Grouping DSL**: `Filter(...)`/`Keep(...)` with composable filter
  expressions (regex/range/istrue/not/and/or), `as(label)` aliases, parallel
  `each()` blocks, multiple sub-groupings, `MaxAll()` (`max(inf)`), multi-key
  ordering, `GroupingBucket` with `inf` and open/closed endpoints, and the
  documented expression functions (`strlen`, `strcat`, `tostring`, `tolong`,
  `todouble`, `toraw`, `size`, `sort`, `reverse`, `at`, `interpolatedlookup`,
  `geo_distance`, `sub`, `xorbit`, `time.dayofweek`, `time.dayofyear`);
  non-numeric grouping outputs preserved via `RawAggregations`.
- **Query API**: `model.language`/`model.encoding`, `collapse.summary`,
  `presentation.template`, `timezone`, `select` (JSON grouping),
  `trace.query`, `totalRerankCount`, `totalKeepRankCount`, second/global phase
  rank-score drop limits, `matchPhase.totalMaxHits`.
- **document/v1**: `create` on conditional puts, `cluster` on GETs,
  group-/number-/root-level visiting, tensor `modify`/`add`/`remove` field
  update operations (with `create` flag and short forms).
- **Feed**: per-document retry with exponential backoff for 429/503 honoring
  `Retry-After`, configurable retained-error cap with `ErrorsTruncated`,
  `HadIgnoredFields` on results.
- **Client & observability**: options-only constructor with full mTLS support
  outside DI, `UseProxy`, `RecordQueryTextInTraces` (query text and document
  ids are excluded from traces by default), named health-check registration,
  metrics tagged with method/host/operation/status (caller cancellations no
  longer counted as errors), `RetryAfter` on `VespaTooManyRequestsException`,
  `VespaError.Transient`, `Coverage.Degraded`, `SummaryFeatures`, metric
  `min`/`average`/`rate`/percentiles.
- **Schema**: `[VespaRankFunction]` attribute, predicate arity/bounds,
  `HostNames`, `StemmingMode.Shortest`, `RankType.About`/`Empty`, wider .NET
  type mapping (`short`, `ushort`, `uint`, `sbyte`, `Guid`, `char`, `byte[]`,
  read-only collection interfaces, `HashSet<T>`).
- 44 new end-to-end integration tests covering schema deployment shapes,
  YQL/grouping acceptance, tensor wire formats and advanced document/v1
  operations against a real Vespa container.

### Security

- `namespace` and `documentType` are URL-escaped in every `/document/v1` path
  (previously injectable); identifier validation on all YQL builder entry
  points (field names, parameter names, sources, sameElement operators);
  query text and document ids no longer exported to OpenTelemetry traces by
  default; http/https-only endpoint validation; certificates disposed with
  their handlers.

### Performance

- Search responses stream (`ResponseHeadersRead`) instead of full buffering;
  allocation-free request timing in metrics; state/metrics endpoints parse
  the response stream directly; code analysis (latest-recommended) enabled as
  errors for all shipped projects.

## [2.0.0] - 2026-07-05

See release notes for the 2.0 API.

## [1.1.0]

## [1.0.0]

Initial release.
