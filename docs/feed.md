# Bulk Feed Operations

Vespa.NET provides high-throughput feed helpers built on Vespa's `/document/v1` API, with parallel HTTP/2 requests, backpressure, and progress tracking.

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
| `TotalDocuments` | Total documents processed |
| `SuccessCount` | Successful operations |
| `FailureCount` | Failed operations (always exact) |
| `IsSuccess` | `true` if no failures |
| `SuccessRate` | `SuccessCount / TotalDocuments` |
| `Duration` | Total elapsed time |
| `Errors` | `IReadOnlyCollection<FeedError>` with per-document error details, capped at `VespaClientOptions.FeedMaxRetainedErrors` (default 1000, first errors win) |
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
