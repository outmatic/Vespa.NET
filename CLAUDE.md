# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Vespa.NET is a high-performance C# client library for [Vespa.ai](https://vespa.ai). It targets .NET 10.0 (primary) and .NET 8.0 (LTS). Solution file: `Vespa.NET.slnx`.

## Commands

```bash
# Build
dotnet build
dotnet build -c Release

# Unit tests
dotnet test tests/Vespa.NET.Tests
dotnet test --filter "Name=<TestMethodName>"
dotnet test --filter "FullyQualifiedName~<ClassName>"

# Integration tests (require Docker; gated by an env var, otherwise they no-op pass)
VESPA_INTEGRATION_TESTS=1 dotnet test tests/Vespa.NET.IntegrationTests
# Optional: VESPA_ENDPOINT / VESPA_CONFIG_ENDPOINT to reuse a running Vespa,
# VESPA_READY_TIMEOUT_SECONDS to extend the readiness wait.

# Format
dotnet format
dotnet format --verify-no-changes

# Package
dotnet pack -c Release

# Benchmarks
dotnet run -c Release --project benchmarks/Vespa.NET.Benchmarks
```

## Architecture

Seven projects, organized as `src/`, `tests/`, `samples/`, `benchmarks/`:

- **`src/Vespa.NET.Models`** (namespace `Vespa.Models`) — DTOs, custom JSON converters (`VespaTensorConverter`, `VespaIdConverter`, `VespaEmbeddingConverter`), attributes (`[VespaDocument]`, `[VespaField]`, `[VespaTensor]`, `[VespaRankProfile]`, …), tensor types, schema enums, the `VespaDocumentId` grammar helper, and the exception hierarchy. No external dependencies.
- **`src/Vespa.NET`** (namespace `Vespa`) — Client implementation. `VespaClient` implements `IVespaClient` and composes **four** operation interfaces plus state/metrics methods:
  - `IDocumentOperations` (`Documents/`) — CRUD, by-selection update/delete/copy with continuation, visiting (paged and JSONL streaming) via `/document/v1/`.
  - `ISearchOperations` (`Search/`) — YQL search, nearest-neighbor, grouping (`GroupByAsync`) via `/search/`; paged/streaming extensions clone the request per page.
  - `IFeedOperations` (`Feed/`) — bulk PUT/UPDATE/DELETE and a streaming `FeedAsync` built on a bounded `Channel<T>` producer/consumer pipeline (backpressure, `Interlocked` counters).
  - `IAdminOperations` (`Admin/`) — schema/application deployment against the config server (port 19071). The admin `HttpClient` is created lazily on first `Admin` access.
  - `Query/` — fluent YQL DSL: `YqlBuilder`/`YqlBuilder<T>`, `YqlWhereClause` (predicate AST in `YqlPredicate`), `GroupingBuilder`, `RankingBuilder`. String values are escaped; identifiers are validated (`YqlIdentifier`). `userQuery()` text travels via `model.queryString`, set by `ToSearchRequest()`/`WithYql()`.
  - `Schema/VespaSchemaBuilder` (namespace `Vespa.Models.Schema`) — reflection-driven `.sd` schema + application-package (ZIP) generation, consumed by `AdminOperations`.
  - Observability: `VespaActivitySource` (tracing), `VespaClientMetrics` + `VespaMetricsHandler` (Meter), `VespaHealthCheck` (ASP.NET health checks), source-generated `[LoggerMessage]` logging.
- **`src/Vespa.NET.Testcontainers`** — `VespaContainer` for integration tests (image pinned to `vespaengine/vespa:8`), depends on the full client.
- **`tests/Vespa.NET.Tests`** — xUnit + Moq unit tests (`MockHttpMessageHandler`, `TestDataFactory` helpers). When testing request bodies, prefer handlers that actually read the content — mocks that ignore the body have hidden disposal bugs before.
- **`tests/Vespa.NET.IntegrationTests`** — E2E tests against a real Vespa container (shared `VespaFixture`, gated by `VESPA_INTEGRATION_TESTS=1`).
- **`samples/Vespa.NET.Samples`** — Console demo using `Spectre.Console`.
- **`benchmarks/Vespa.NET.Benchmarks`** — BenchmarkDotNet (JSON + YQL builder).

### Key design decisions

- **DI integration:** `VespaServiceCollectionExtensions` registers the client via `IHttpClientFactory` with configurable resilience (retry + circuit breaker via `Microsoft.Extensions.Http.Resilience`/Polly). The DI path activates `VespaClient` with an internal `httpClientPreconfigured` flag so constructor defaults never clobber the factory/user configuration.
- **JSON:** `System.Text.Json` with snake_case naming (`JsonNamingPolicy.SnakeCaseLower`), null-ignorance, and a single static `JsonSerializerOptions` (`VespaJsonOptions.Default`). Tensors and IDs use custom converters.
- **Vespa correctness:** when touching YQL generation, tensor JSON, document JSON (updates), or schema generation, verify against the official references on docs.vespa.ai (query-language-reference, grouping-syntax, document-json-format, document-v1-api-reference, schema-reference) — several past bugs came from plausible-but-wrong syntax that unit tests with mocks cannot catch.
- **Async everywhere:** all I/O is `async`/`await` with `CancellationToken` support. Probe-style methods (`HealthCheckAsync`, …) rethrow only the caller's cancellation; HTTP timeouts count as failed probes.
- **Null safety:** `#nullable enable` in all projects; constructor guards via `ArgumentNullException`.
- **Performance:** HTTP/2 (fallback to 1.1), `SocketsHttpHandler` pooling, GZip/Deflate decompression at the handler, `HttpCompletionOption.ResponseHeadersRead`.
- **URL construction:** `/document/v1/{namespace}/{documentType}/docid/{documentId}` with `Uri.EscapeDataString()`; document IDs are normalized via `VespaDocumentId.GetUserSpecified` (full id grammar, user part may contain `:`).
- **Packaging:** `Directory.Build.props` carries shared NuGet metadata, deterministic builds, embedded PDBs, and SourceLink for the three packable `src/` projects.

### Conventions

- Interfaces prefixed with `I` (`IVespaClient`, `IDocumentOperations`)
- Async methods suffixed with `Async`
- Private fields: `_camelCase`
- Immutable options object: `VespaClientOptions` (`init`-only)
- Thread-safe bulk counters via `Interlocked.Increment`

## C# Style

- Target framework: **net10.0**, C# 14+
- Always prefer modern constructs:
  - Primary constructors (`class MyService(ILogger logger)`)
  - Collection expressions (`var list = [1, 2, 3]`)
  - Pattern matching with `switch` expressions and list patterns
  - `required` properties instead of constructor validation where appropriate
  - `record` and `record struct` for immutable data models
  - `init`-only setters
  - Raw string literals (`"""..."""`) for multiline strings
  - `file`-scoped types where applicable
  - Null-coalescing assignment (`??=`) and null-conditional operators
  - `async`/`await` with `ValueTask` where appropriate
  - Top-level statements for entry points
  - Generic math interfaces (`INumber<T>`, etc.) where relevant

## C# Coding Style

### Braces & Formatting
- Omit braces for single-statement `if`, `else`, `for`, `foreach`, `while`
- Use braces only for multi-statement blocks
- Prefer compact, readable code over verbose formatting

**Prefer:**
```csharp
if (condition)
    DoSomething();

if (condition)
    DoSomething();
else
    DoOther();

foreach (var item in list)
    Process(item);
```

**Avoid:**
```csharp
if (condition)
{
    DoSomething();
}
```
