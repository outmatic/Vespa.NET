# Document Operations

Complete reference for Vespa.NET's document CRUD, field-level updates, conditional writes, addressing modes, selection operations, and visit/iterate APIs.

---

## CRUD

```csharp
var product = new Product { Id = "p-1", Name = "Laptop", Price = 999.99m };

// Create or replace
await client.Documents.PutAsync(product.Id, product, documentType: "product");

// Read (returns null if not found)
var doc = await client.Documents.GetAsync<Product>(product.Id, documentType: "product");

// Partial update — Vespa requires field operations (assign, increment, …).
// See the "Field-Level Updates" section below for the typed fluent builder.
await client.Documents.UpdateFieldsAsync(product.Id,
    new Dictionary<string, FieldOperation> { ["price"] = FieldOp.Assign(49.99m) },
    documentType: "product", createIfMissing: true);

// Delete
await client.Documents.DeleteAsync(product.Id, documentType: "product");
```

> [!TIP]
> With `[VespaDocument]` on your model, `documentType` and `namespace` are inferred automatically — no string arguments needed.

---

## Conditional Writes

```csharp
await client.Documents.PutAsync(product.Id, product,
    condition: "product.price > 0");  // HTTP 412 → VespaConditionNotMetException
```

---

## Field-Level Updates

```csharp
await client.Documents.UpdateFieldsAsync(product.Id, new Dictionary<string, FieldOperation>
{
    ["view_count"] = FieldOp.Increment(1),
    ["price"]      = FieldOp.Assign(99.99m),
    ["tags"]       = FieldOp.Add("sale"),
    ["embedding"]  = FieldOp.Modify("replace", [new TensorCellUpdate(new() { ["x"] = "0" }, 5.0)]),
    ["old_field"]  = FieldOp.ClearField()
}, documentType: "product");
```

### Available Operations

| Operation | Description |
|---|---|
| `FieldOp.Assign(value)` | Overwrite field value |
| `FieldOp.Increment(n)` | Add to numeric field |
| `FieldOp.Decrement(n)` | Subtract from numeric field |
| `FieldOp.Multiply(n)` | Multiply numeric field |
| `FieldOp.Divide(n)` | Divide numeric field |
| `FieldOp.Add(value)` | Add element to array/weightedset |
| `FieldOp.Remove(value)` | Remove element from array/weightedset |
| `FieldOp.Match(predicate)` | Match-based update |
| `FieldOp.ClearField()` | Remove field entirely |
| `FieldOp.Modify(op, cells)` | Tensor cell-level update |

### Typed Lambda Builder

```csharp
await client.Documents.UpdateFieldsAsync<Product>(product.Id, ops => ops
    .Field(p => p.Name, FieldOp.Assign("New Name"))
    .Field(p => p.Price, FieldOp.Multiply(0.9)));
```

---

## Fieldpath Updates (Nested Structures)

```csharp
await client.Documents.UpdateFieldsAsync(product.Id, new Dictionary<string, FieldOperation>
{
    [FieldPath.Struct("address", "city")]  = FieldOp.Assign("Oslo"),
    [FieldPath.Map("tags", "color")]       = FieldOp.Assign("red"),
    [FieldPath.Array("items", 0)]          = FieldOp.Assign("new-item")
}, documentType: "product");
```

---

## Document Addressing

Every CRUD verb has matching `...ByGroupAsync` and `...ByNumberAsync` variants that
use Vespa's group/number document-ID modifiers instead of a plain document ID.

```csharp
// Read
await client.Documents.GetByGroupAsync<Product>(
    group: "tenant-42", localId: product.Id, documentType: "product");
await client.Documents.GetByNumberAsync<Product>(
    number: 42L, localId: product.Id, documentType: "product");

// Write
await client.Documents.PutByGroupAsync("tenant-42", product.Id, product, documentType: "product");
await client.Documents.PutByNumberAsync(42L, product.Id, product, documentType: "product");

// Partial update (field operations — Vespa requires these, not raw values)
await client.Documents.UpdateFieldsByGroupAsync("tenant-42", product.Id,
    new() { ["price"] = FieldOp.Multiply(0.9) }, documentType: "product",
    createIfMissing: true);
await client.Documents.UpdateFieldsByNumberAsync(42L, product.Id,
    new() { ["view_count"] = FieldOp.Increment(1) }, documentType: "product");

// Delete
await client.Documents.DeleteByGroupAsync("tenant-42", product.Id, documentType: "product");
await client.Documents.DeleteByNumberAsync(42L, product.Id, documentType: "product");
```

> [!NOTE]
> Group/number addressing is primarily useful for document types in `streaming` or
> `store-only` mode; in `index` mode Vespa assigns buckets automatically regardless
> of this addressing. Use normal docid addressing for regular indexed document types.

---

## Selection-Based Operations

Selection operations apply a Vespa selection expression across an entire document
type — no document ID required. Vespa processes them in time-bounded chunks and
returns a `continuation` token while more work remains; Vespa.NET **automatically
loops on that token** until the operation completes, and the returned
`VespaResponse.DocumentCount` is the sum of documents affected across all chunks.

```csharp
// Update all matching documents (loops internally until complete)
var result = await client.Documents.UpdateBySelectionAsync(
    selection: "product.price < 10",
    fieldOperations: new() { ["price"] = FieldOp.Assign(10.0) },
    documentType: "product",
    cluster: "content");

Console.WriteLine($"Updated {result.DocumentCount} documents");

// Delete all matching documents
await client.Documents.DeleteBySelectionAsync(
    selection: "product.discontinued == true",
    documentType: "product",
    cluster: "content");

// Copy all matching documents from one content cluster to another
await client.Documents.CopyBySelectionAsync(
    selection: "product.year < 2000",
    documentType: "product",
    cluster: "content",
    destinationCluster: "archive");
```

`cluster` identifies the source content cluster and is required by Vespa whenever
the target is ambiguous — pass it explicitly. `CopyBySelectionAsync` takes
`destinationCluster` as a dedicated argument (it is no longer a field on
`DocumentRequestOptions`).

### SelectionRequestOptions

Selection operations take a `SelectionRequestOptions` record instead of
`DocumentRequestOptions`. Each field maps to a Vespa query parameter on
`/document/v1`:

| Field | Maps to | Notes |
|---|---|---|
| `TimeChunk` | `timeChunk` | Target processing time per chunk (Vespa default: 60s) |
| `BucketSpace` | `bucketSpace` | e.g. `default`, `global` |
| `Timeout` | `timeout` | Per-request Vespa soft timeout, serialized as `{ms}ms` |
| `TraceLevel` | `tracelevel` | Diagnostic trace level (0-9) |
| `Stream` | `stream=true` + `Accept: application/jsonl` | Visit more buckets per HTTP call (JSONL response). Update/Delete only — not supported on Copy |

```csharp
await client.Documents.DeleteBySelectionAsync(
    selection: "product.discontinued == true",
    documentType: "product",
    cluster: "content",
    requestOptions: new SelectionRequestOptions
    {
        TimeChunk = TimeSpan.FromSeconds(30),
        Timeout   = TimeSpan.FromMinutes(5),
    });
```

> [!TIP]
> For selections that touch millions of documents, set `Stream = true` on
> `UpdateBySelectionAsync` / `DeleteBySelectionAsync`. The client still loops on
> the continuation token, but each HTTP request covers many more buckets, cutting
> the round-trip count (and its latency overhead) substantially. The flag is
> ignored internally when passed to `CopyBySelectionAsync` — Vespa does not accept
> `stream=true` on the copy endpoint, so the client throws `ArgumentException`.

### Manual Pagination (Crash-Safe Resume)

Each selection operation has a `...PageAsync` variant that performs exactly one
HTTP call, returns a `SelectionPageResult`, and lets you drive the loop yourself.
Use this when you need to persist the continuation token between chunks — for
example, to resume a long-running backfill after a crash or deployment.

`SelectionPageResult` exposes:

- `DocumentCount` — documents affected **in this chunk only** (not cumulative).
- `Continuation` — token to pass back to resume; `null` when complete.
- `IsComplete` — `true` iff `Continuation is null`.
- `StatusCode`, `IgnoredFields` — same as `VespaResponse`.

```csharp
string? token = await LoadCheckpoint();  // e.g. from database, null on first run
long totalUpdated = 0;

while (true)
{
    var page = await client.Documents.UpdateBySelectionPageAsync(
        selection: "product.price < 10",
        fieldOperations: new() { ["price"] = FieldOp.Assign(10.0) },
        documentType: "product",
        cluster: "content",
        continuation: token);

    totalUpdated += page.DocumentCount;

    if (page.IsComplete)
        break;

    token = page.Continuation;
    await SaveCheckpoint(token);  // durable write between chunks
}

await ClearCheckpoint();
```

> [!TIP]
> For crash-safe resume, write `page.Continuation` to durable storage between
> chunks. The auto-looping `UpdateBySelectionAsync` / `DeleteBySelectionAsync` /
> `CopyBySelectionAsync` methods are simpler but will restart from scratch if
> the process dies mid-run.

The same pattern applies to `DeleteBySelectionPageAsync` and `CopyBySelectionPageAsync`.

---

## Visit / Iterate

### Standard Visit (continuation tokens)

```csharp
await foreach (var doc in client.Documents.VisitAsync<Product>(
    documentType: "product",
    selection: "product.price > 100",
    wantedDocumentCount: 1000,
    slices: 4, sliceId: 0,          // parallel slicing
    fromTimestamp: 1700000000,       // time-bounded
    includeRemoves: true))
{
    Process(doc.Fields);
}
```

### JSONL Streaming (lower memory)

`VisitJsonlAsync` streams one document per line (`Accept: application/jsonl`) and
supports the same full parameter surface as `VisitAsync` — `timeout`, `slices`,
`sliceId`, `concurrency`, `fromTimestamp`, `toTimestamp`, `includeRemoves`, and
`bucketSpace`. Prefer it for large result sets.

```csharp
await foreach (var doc in client.Documents.VisitJsonlAsync<Product>(
    documentType: "product",
    selection: "product.price > 100",
    slices: 8, sliceId: 0,
    concurrency: 4,
    timeout: TimeSpan.FromMinutes(2),
    bucketSpace: "default"))
{
    Process(doc.Fields);
}
```

---

## Request Options

All single-document operations accept `DocumentRequestOptions` for fine-grained control:

```csharp
var opts = new DocumentRequestOptions
{
    Route        = "default/chain.indexing",
    TraceLevel   = 5,
    TensorFormat = "short",
    DryRun       = true,
    Timeout      = TimeSpan.FromSeconds(10),   // Vespa server-side soft timeout
};

await client.Documents.PutAsync(product.Id, product, documentType: "product", requestOptions: opts);
```

> [!NOTE]
> `DocumentRequestOptions.Timeout` is the **Vespa server-side soft timeout** — it
> tells Vespa to abandon the request after the given duration and release its
> resources. It is distinct from `VespaClientOptions.Timeout`, which is the
> **HTTP client timeout** that aborts the TCP request from the client side. Set
> the server-side timeout a bit lower than the HTTP timeout so Vespa can return
> a clean response before the client gives up.

For selection operations, use `SelectionRequestOptions` instead (see
[Selection-Based Operations](#selection-based-operations)).

## Cancellation

All document operations and visit APIs accept a `CancellationToken`.

- Request cancellation is propagated to the caller as `OperationCanceledException`.
