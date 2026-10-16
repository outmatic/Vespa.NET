# Bulk Feed Operations

Vespa.NET provides high-throughput feed helpers built on Vespa's `/document/v1` API, with parallel HTTP/2 requests, backpressure, and progress tracking.

---

## Mixed Operations

Feed puts, field updates and deletes through one call, including operations for
different schemas and namespaces. Both `IEnumerable<FeedOperation>` and
`IAsyncEnumerable<FeedOperation>` are accepted:

```csharp
using Vespa.Feed;
using Vespa.Models;

var requestOptions = new DocumentRequestOptions { Timeout = TimeSpan.FromSeconds(10) };
FeedOperation[] operations =
[
    FeedOperation.Put("p-1", product, requestOptions: requestOptions),
    FeedOperation.Update<Product>("p-1", update => update
        .Field(p => p.Price, FieldOp.Assign(49.99m)),
        condition: "product.price > 0", requestOptions: requestOptions),
    FeedOperation.Delete<Product>("p-2", requestOptions: requestOptions)
];

var result = await client.Feed.FeedAsync(
    operations,
    new FeedOptions
    {
        MaxConcurrency = 32,
        BoundedCapacity = 128,
        OnProgress = progress => Console.WriteLine(
            $"{progress.ProcessedCount}: {progress.LastOperationKind} {progress.LastDocumentType}/{progress.LastDocumentId}")
    },
    cancellationToken);

Console.WriteLine($"{result.SuccessCount}/{result.TotalOperations} operations succeeded");
```

Factories infer schema and namespace from `[VespaDocument]`. Use `@namespace` to
override the model's namespace. Supplying `documentType` bypasses attribute
inference and supports models without attributes; an omitted namespace then uses
`VespaClientOptions.DefaultNamespace`:

```csharp
FeedOperation.Put("p-3", new { product_name = "Camera", price = 100.0 },
    documentType: "product", @namespace: "archive");

FeedOperation.Update("p-3",
    new Dictionary<string, FieldOperation> { ["price"] = FieldOp.Assign(90.0) },
    documentType: "product", @namespace: "archive", createIfMissing: true);

FeedOperation.Delete("p-3", documentType: "product", @namespace: "archive");
```

`Put` supports `condition` and `create` (conditional put with create).
`Update` supports `condition` and `createIfMissing`, with either a typed builder or
a dictionary of Vespa field operations. `Delete` supports `condition`. Each factory
accepts `DocumentRequestOptions`, including routing, dry-run, timeout and tracing.
Keep payload objects unchanged until the feed completes; update dictionaries are
shallow-copied when the operation is constructed.

### Ordering and Backpressure

- Within one mixed-feed call, mutations of the same document execute in source
  order, including retries. A failed operation is recorded and the next operation
  for that document is still attempted.
- Identity includes namespace, schema, group/number location and user ID. Full and
  bare IDs addressing the same document share an ordered queue. Full IDs must agree
  with the operation's target schema and namespace, as with regular document CRUD.
- Different documents can execute concurrently. There is no global completion
  order or coordination between separate feed calls or clients.
- `MaxConcurrency` (default 64) limits in-flight operations. `BoundedCapacity`
  (default 256) limits queued operations across all documents, including operations
  waiting behind a busy document. The source can have one additional prefetched entry.

Retries follow the same rules described below. Ordering does not provide
exactly-once execution: replaying non-idempotent updates such as increments can
apply them more than once. Use test-and-set conditions when needed; see
[Vespa reads and writes](https://docs.vespa.ai/en/writing/reads-and-writes.html).

For mixed feeds, `FeedError` includes `OperationKind`, `DocumentType`, the resolved
`Namespace`, and a zero-based `OperationIndex` in the source. These distinguish
mutations with the same local ID. `FeedProgress` provides the corresponding
`LastOperationKind`, `LastDocumentType`, `LastNamespace`, and `OperationIndex`.
The additional metadata is null for the existing bulk and generic feed APIs.

`FeedResult.TotalOperations` counts each mutation, even when several target the
same document. `TotalDocuments` remains available with the same value. Individual
errors, partial results on source/callback failure, cancellation and retained-error
limits follow the rules below. A null entry faults the source; accepted operations
are drained before the resulting `VespaFeedException` is returned.

---

## BulkPut / BulkUpdate / BulkDelete

```csharp
using Vespa.Feed;
using Vespa.Models;

var docs = products.Select(p => new FeedDocument<Product> { Id = p.Id, Fields = p });

var result = await client.Feed.BulkPutAsync(docs, documentType: "product", maxConcurrency: 20);
Console.WriteLine($"{result.SuccessCount}/{result.TotalDocuments} in {result.Duration.TotalSeconds:F2}s");

// Bulk update — each request carries explicit Vespa field operations, since
// /document/v1 PUT rejects raw partial documents.
var updates = products.Select(p => new BulkFieldUpdate
{
    Id = p.Id,
    FieldOperations = new() { ["price"] = FieldOp.Assign(p.Price) }
});
await client.Feed.BulkUpdateAsync(updates, documentType: "product", createIfMissing: true);

// Bulk delete
await client.Feed.BulkDeleteAsync(["id-1", "id-2", "id-3"], documentType: "product");
```

---

## Streaming Feed Pipeline

For high-throughput scenarios, `FeedAsync` uses `IAsyncEnumerable<T>` with a bounded `Channel<T>`. Documents are consumed on-demand with backpressure — no need to materialize the entire dataset:

```csharp
async IAsyncEnumerable<FeedDocument<Product>> ReadFromDatabase()
{
    await foreach (var product in db.Products.AsAsyncEnumerable())
        yield return new FeedDocument<Product> { Id = product.Id, Fields = product };
}

var result = await client.Feed.FeedAsync(
    ReadFromDatabase(),
    documentType: "product",
    maxConcurrency: 64,       // parallel HTTP/2 streams
    boundedCapacity: 256,     // backpressure buffer
    onProgress: p => Console.Write($"\r{p.SuccessCount} docs fed..."));

Console.WriteLine($"\n{result.SuccessCount}/{result.TotalDocuments} in {result.Duration.TotalSeconds:F2}s");
```

---

## Per-Document Conditions

```csharp
var docs = products.Select(p => new FeedDocument<Product>
{
    Id = p.Id,
    Fields = p,
    Condition = "product.version == 1"  // per-document conditional write
});
```

---

## Progress Callback

`onProgress` is invoked **concurrently** from multiple consumer tasks — it must be thread-safe. Each `FeedProgress` snapshot carries:

- `ProcessedCount` — unique and monotonically increasing per invocation (success + failure)
- `SuccessCount` / `FailureCount` — coherent with the snapshot: their sum is always ≥ `ProcessedCount`
- `LastDocumentId` — the document the snapshot was emitted for

A throwing callback faults the pipeline (see [Error Handling](#error-handling)).

---

## Automatic Retry (429 / 503)

When `VespaClientOptions.EnableRetry` is set (the default), the feed pipeline re-sends individual document operations that fail with HTTP 429 (`VespaTooManyRequestsException`) or HTTP 503 (`VespaServerException`) — Vespa's transient backpressure signals:

- The server's `Retry-After` header is honored when present; otherwise exponential backoff from `VespaClientOptions.FeedRetryBaseDelay` (default 200 ms) with jitter, capped at 30 s.
- At most `VespaClientOptions.MaxRetryAttempts` extra attempts per document (default 3); once exhausted, the document counts as a single failure.
- Clients registered via DI (`AddVespaClient`) *additionally* retry transient responses at the HTTP-handler level (Polly). When both are active the handler retries first — each feed-level attempt may itself have been retried at the HTTP level.

---

## FeedResult

| Property | Description |
|---|---|
| `TotalDocuments` | Total feed entries accepted from the source |
| `TotalOperations` | Alias of `TotalDocuments`; mixed-feed mutations are counted separately |
| `SuccessCount` | Successful operations |
| `FailureCount` | Failed operations (always exact) |
| `IsSuccess` | `true` if no failures |
| `SuccessRate` | `SuccessCount / TotalDocuments` |
| `Duration` | Total elapsed time |
| `Errors` | `IReadOnlyList<FeedError>` with per-operation error details, capped at `VespaClientOptions.FeedMaxRetainedErrors` (default 1000, first errors win) |
| `ErrorsTruncated` | `true` when more errors occurred than `Errors` retains — counts stay exact either way |
| `HadIgnoredFields` | `true` when any response signalled ignored fields (`X-Vespa-Ignored-Fields`), typically a document/schema mismatch |

`FeedResult` and `FeedError` live in the `Vespa.Models` namespace.

---

## Error Handling

Individual document failures never stop the feed — they are counted and recorded in `FeedResult.Errors`.

If the **pipeline itself** faults (the source enumeration throws, or the `onProgress` callback throws), the partial progress is not lost: a `VespaFeedException` is thrown carrying the partial `FeedResult` and the original fault as `InnerException` (an `AggregateException` when several independent faults occurred):

```csharp
try
{
    await client.Feed.FeedAsync(ReadFromDatabase(), documentType: "product");
}
catch (VespaFeedException ex)
{
    Console.WriteLine($"Feed faulted after {ex.PartialResult.SuccessCount} documents: {ex.InnerException}");
}
```

When the source enumeration throws mid-feed, documents already buffered in the channel are still sent before the fault surfaces; their outcomes are included in `PartialResult`.

## Cancellation

All bulk/feed APIs accept a `CancellationToken`.

- When cancellation is requested, the pipeline stops and propagates `OperationCanceledException` — never `VespaFeedException`.
- Cancelled work is not counted as a per-document failure.
