# Bulk Feed Operations

Vespa.NET provides high-throughput feed helpers built on Vespa's `/document/v1` API, with parallel HTTP/2 requests, backpressure, and progress tracking.

---

## Mixed Operations

Feed puts, field updates and deletes through one call, including operations for different schemas and namespaces. Both `IEnumerable<FeedOperation>` and `IAsyncEnumerable<FeedOperation>` are accepted. The examples below use the planned 4.0 mixed-feed factories:

```csharp
using Vespa.Documents;
using Vespa.Feed;
using Vespa.Models;

var requestOptions = new DocumentRequestOptions { Timeout = TimeSpan.FromSeconds(10) };
FeedOperation[] operations =
[
    FeedOperation.Put("p-1", product, requestOptions: requestOptions),
    FeedOperation.Update<Product>("p-1", update => update
        .Field(p => p.Price, FieldOp.Assign(49.99m)),
        condition: p => p.Price > 0, requestOptions: requestOptions),
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

Factories infer schema and namespace from `[VespaDocument]`. Use `@namespace` to override the model's namespace. Supplying `documentType` bypasses attribute inference and supports models without attributes; an omitted namespace then uses `VespaClientOptions.DefaultNamespace`:

```csharp
FeedOperation.Put("p-3", new { product_name = "Camera", price = 100.0 },
    documentType: "product", @namespace: "archive");

FeedOperation.Update("p-3",
    new Dictionary<string, FieldOperation> { ["price"] = FieldOp.Assign(90.0) },
    documentType: "product", @namespace: "archive", createIfMissing: true);

FeedOperation.Delete("p-3", documentType: "product", @namespace: "archive");
```

`Put` supports conditional put with `create`. `Update` supports `createIfMissing`, with either a typed builder or a dictionary of Vespa field operations. Each factory accepts `DocumentRequestOptions`, including routing, dry-run, timeout and tracing. Keep payload objects unchanged until the feed completes; update dictionaries are shallow-copied when the operation is constructed.

### Conditions

In 4.0, the four generic factories use `condition:` for a lambda and `rawCondition:` for a native Vespa condition. The target arguments come first, then the condition pair, creation flag where applicable and request options:

```csharp
var update = FeedOperation.Update<Product>("p-1",
    fields => fields.Field(p => p.Price, FieldOp.Assign(49.99m)),
    condition: p => p.Price > 0,
    createIfMissing: true);

var delete = FeedOperation.Delete<Product>("p-2",
    rawCondition: "product.price <= 0");
```

The operation is unconditional when both condition arguments are omitted or null. If both are non-null, the factory throws `ArgumentException` before running an update callback. Native condition strings are stored unchanged; the non-generic `Update` and `Delete` factories retain their string `condition:` argument.

Typed predicates use the actual resolved document type. For example, `FeedOperation.Put("p-1", product, documentType: "product_v2", condition: p => p.Price > 0)` qualifies the field as `product_v2.price`, even if the model's attribute names another schema. The explicit target changes the document-type qualifier; field names still come from the selected properties on `T` and their mappings. Models without `[VespaDocument]` work when you supply `documentType`. Keep the predicate's model type consistent with the payload; a pre-typed base-class predicate can affect generic type inference and select the base model's metadata and serialization contract.

Captured values are read when the factory is called, before the update callback. The resulting `FeedOperation.Condition` is a native string that stays fixed while the operation waits in the queue and across retries. A lazy source may call the factory during enumeration; constructing that source does not construct its operations.

The factory renders a typed condition on every call, even when you reuse the expression. When every operation uses the same condition and schema, render it once before the feed loop and reuse it through `rawCondition:`. In a client-only .NET 10 benchmark, rendering a fixed predicate took roughly two orders of magnitude longer than passing the rendered string.

```csharp
var condition = DocumentCondition.For<Product>(p => p.Price > 0);
var operations = products.Select(product =>
    FeedOperation.Put(product.Id, product, rawCondition: condition));
await client.Feed.FeedAsync(operations);
```

When each document has its own threshold, capture that value when you construct the operation. The typed condition formats the threshold using a decimal point even under cultures such as `it-IT`.

```csharp
var operations = updates.Select(update =>
{
    var minimum = update.MinimumStoredPrice;
    return FeedOperation.Put(update.Product.Id, update.Product,
        condition: stored => stored.Price > minimum);
});
```

In a hot loop, keep the field and operator fixed and pass a `rawCondition:` that formats just the value with `DocumentCondition.FormatLiteral`. In the same benchmark, rendering a predicate with a different threshold on every call took about 20 times as long as formatting just the threshold. The helper uses the same number formatting and string escaping as typed write conditions. Ordinary interpolation under `it-IT` would put `100,5` in the selector. Vespa rejects that syntax, and the feed records the parser error in `FeedResult.Errors`.

```csharp
var operations = updates.Select(update =>
    FeedOperation.Put(update.Product.Id, update.Product,
        rawCondition: $"product.price > {DocumentCondition.FormatLiteral(update.MinimumStoredPrice)}"));
```

A typed condition is evaluated against the stored document and does not add a document-type guard. `p => true` renders native `true`. Individual HTTP 412 failures are recorded in `FeedResult.Errors`; later operations for the same document are still attempted. See [Conditional writes](document-operations.md#conditional-writes) for supported expressions and native evaluation rules.

### Ordering and Backpressure

- Within one mixed-feed call, mutations of the same document execute in source order, including retries. A failed operation is recorded and the next operation for that document is still attempted.
- Identity includes namespace, schema, group/number location and user ID. Full and bare IDs addressing the same document share an ordered queue. Full IDs must agree with the operation's target schema and namespace, as with regular document CRUD.
- Different documents can execute concurrently. There is no global completion order or coordination between separate feed calls or clients.
- `MaxConcurrency` (default 64) limits in-flight operations. `BoundedCapacity` (default 256) limits queued operations across all documents, including operations waiting behind a busy document. The source can have one additional prefetched entry.

Retries follow the same rules described below. Ordering does not provide exactly-once execution: replaying non-idempotent updates such as increments can apply them more than once. Use test-and-set conditions when needed; see [Vespa reads and writes](https://docs.vespa.ai/en/writing/reads-and-writes.html).

For mixed feeds, `FeedError` includes `OperationKind`, `DocumentType`, the resolved `Namespace`, and a zero-based `OperationIndex` in the source. These distinguish mutations with the same local ID. `FeedProgress` provides the corresponding `LastOperationKind`, `LastDocumentType`, `LastNamespace`, and `OperationIndex`. The additional metadata is null for the existing bulk and generic feed APIs.

Use `FeedResult.TotalOperations` when reporting a feed result. A null entry faults the source. The feed drains accepted operations before surfacing the resulting `VespaFeedException`. See [FeedResult](#feedresult) for count and error details.

---

## BulkPut / BulkUpdate / BulkDelete

Pass a lazy `IEnumerable` to produce entries as the bulk feed needs them. The client pauses enumeration while the channel is full. Use `FeedAsync` when the source is asynchronous or you want to set the buffer size or receive progress callbacks.

```csharp
using Vespa.Feed;
using Vespa.Models;

var docs = products.Select(p => new FeedDocument<Product> { Id = p.Id, Fields = p });

var result = await client.Feed.BulkPutAsync(docs, documentType: "product", maxConcurrency: 20);
Console.WriteLine($"{result.SuccessCount}/{result.TotalOperations} in {result.Duration.TotalSeconds:F2}s");

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

Console.WriteLine($"\n{result.SuccessCount}/{result.TotalOperations} in {result.Duration.TotalSeconds:F2}s");
```

---

## Per-Document Conditions

In 4.0, use `FeedDocument.Create(...)` to construct a homogeneous PUT entry with a typed condition. The payload supplies the model type:

```csharp
var docs = products.Select(product => FeedDocument.Create(
    product.Id, product,
    condition: stored => stored.Price > 100));

var result = await client.Feed.BulkPutAsync(docs,
    documentType: "product", @namespace: "catalog");
```

For field updates, use `BulkFieldUpdate.Create<Product>` with a dictionary or a callback. The condition is rendered before the callback runs, and the field-operation dictionary is shallow-copied:

```csharp
var updates = products.Select(product => BulkFieldUpdate.Create<Product>(
    product.Id,
    update => update.Field(p => p.Price, FieldOp.Assign(product.Price)),
    condition: stored => stored.Price > 0));

await client.Feed.BulkUpdateAsync(updates,
    documentType: "product", @namespace: "catalog");
```

The factory accepts `documentType:` for models without attributes or an explicit schema override. A typed condition otherwise uses the model's `[VespaDocument]` schema. The batch supplies the namespace in every case; the model's namespace annotation is not used for these homogeneous entries. Both condition arguments omitted or null mean unconditional; conflicting non-null `condition` and `rawCondition` values throw before the update callback.

An explicit schema binds the entry even when its condition is native or absent. Without an explicit schema, raw and unconditional entries remain unbound and need no document attribute. The actual request target is still the batch's `documentType`; the entry records the schema it expects, rather than choosing another route.

The built-in feed client compares that expected schema with the batch schema before sending the entry. A mismatch produces a `FeedError` with no HTTP status, sends no request for that entry and allows the other entries to continue. Schema names are compared exactly, including case. This check belongs to the built-in `FeedOperations`; third-party implementations retain their existing interface contract.

Copies made with `with` retain the expected schema, including copies that change or clear `Condition`. Construct a new entry to target another schema. Conditions are rendered when the factory runs and stay fixed during queuing and retries. A lazy source may invoke the factory during enumeration. Keep payloads and field-operation objects unchanged until the feed completes.

Two entries can have identical public properties and still compare unequal if they bind to different schemas. The generated `ToString()` omits that binding.

Existing initializers and native condition strings remain supported:

```csharp
var docs = products.Select(p => new FeedDocument<Product>
{
    Id = p.Id,
    Fields = p,
    Condition = "product.price > 100"
});
```

The schema binding is kept in memory and omitted from JSON. Deserializing a `FeedDocument<T>` recreates a raw entry without that binding. Persist the intended batch schema separately and reconstruct factory entries when the check is needed on replay. The existing `FieldOperation` converter writes operations but does not read them, so replaying populated update DTOs requires reconstructing their field operations.

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

`TotalOperations` counts entries accepted from the source. For example, a put and an update for the same document count as two operations. Any retries belong to their original operation.

Use `TotalDocuments` when constructing a `FeedResult` with an object initializer. By default, System.Text.Json writes both properties and reads `TotalDocuments` to set the count returned by `TotalOperations`.

| Property | Description |
|---|---|
| `TotalOperations` | Number of operations accepted from the source |
| `TotalDocuments` | Same count, with an initializer for callers that construct results |
| `SuccessCount` | Successful operations |
| `FailureCount` | Failed operations (always exact) |
| `IsSuccess` | `true` if no failures |
| `SuccessRate` | `SuccessCount / TotalOperations`, or zero for an empty feed |
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
