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

## FeedResult

| Property | Description |
|---|---|
| `TotalDocuments` | Total documents processed |
| `SuccessCount` | Successful operations |
| `FailureCount` | Failed operations |
| `IsSuccess` | `true` if no failures |
| `SuccessRate` | `SuccessCount / TotalDocuments` |
| `Duration` | Total elapsed time |
| `Errors` | `ConcurrentQueue<FeedError>` with per-document error details |

## Cancellation

All bulk/feed APIs accept a `CancellationToken`.

- When cancellation is requested, the pipeline stops and propagates `OperationCanceledException`.
- Cancelled work is not counted as a per-document failure.
