# Document Operations

Complete reference for Vespa.NET's document CRUD, field-level updates, conditional writes, addressing modes, selection operations, and visit/iterate APIs. The model-aware write signatures described here are intended for the upcoming 4.0 release; see [Migrating to 4.0](migration-4.0.md) when updating an existing application.

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

Use a lambda to write a condition against a model decorated with `[VespaDocument]`. Property names follow `[VespaField(Name = "...")]`, or snake_case when no name is specified.

```csharp
using Vespa.Documents;

await client.Documents.PutAsync(product.Id, product,
    condition: p => p.Price > 0);

await client.Documents.UpdateFieldsAsync<Product>(product.Id,
    update => update.Field(p => p.Price, FieldOp.Assign(49.99m)),
    condition: p => p.Price > 0 && p.Name != null);

await client.Documents.DeleteAsync<Product>(product.Id,
    condition: p => p.Price <= 0);

// Vespa ≥ 8.178: create an absent document; otherwise check its stored price.
await client.Documents.PutAsync(product.Id, product,
    condition: p => p.Price > 0, create: true);
```

Vespa evaluates the condition atomically against the **stored document**, before applying the write. A failed condition throws `VespaConditionNotMetException` (HTTP 412). For a put with `create: true`, an absent document is created without evaluating the condition; an existing document must still satisfy it. This checks the previous state, so validate the incoming product separately when needed.

Predicates support `==`, `!=`, `<`, `<=`, `>`, `>=`, `&&`, `||`, `!`, boolean properties, and null checks. Use constants or captured local variables for values. Strings use exact, case-sensitive equality: `*` and `?` stay literal characters. Nullable numeric comparisons include presence checks so negating a comparison still handles a missing value as C# does. Other comparisons follow Vespa's [document selector semantics](https://docs.vespa.ai/en/reference/writing/document-selector-language.html#comparisons-with-missing-fields-null-values).

The same signatures are available for group and number addressing. Pass `requestOptions:` and `cancellationToken:` as named arguments when needed. Nested property access, method calls such as `Contains`, arithmetic, and casts that lose precision throw `NotSupportedException` before sending a request. The client reads captured local fields without compiling the predicate or invoking property getters.

Typed document conditions and updates select properties that have their own JSON binding. Document IDs, extra-fields containers and unconditionally ignored properties are rejected. A property ignored by JSON may still name a field populated by another writer; typed YQL can query that field, and native condition strings or explicit update field names remain available for document operations outside the model's binding.

The resolved field name must match the name used in document JSON. If an override inherits a different field alias, declare `[VespaField(Name = "...")]` on the override too. Use this attribute for custom JSON names as well; it takes precedence over `[JsonPropertyName]`. Builders reject a remaining mismatch or an ambiguous hidden property.

For an unconditional write, omit both condition arguments. `condition: null` and `condition: default` also mean no typed condition. To use a native Vespa selector string, pass `rawCondition:`:

```csharp
await client.Documents.PutAsync(product.Id, product,
    rawCondition: "product.price > 0");
```

Set either `condition` or `rawCondition`. Supplying both with non-null values throws `ArgumentException` before a builder callback runs or a request is sent. Native selector strings support document IDs, functions, and collection field paths. Use named arguments for conditions: a positional string after the document is interpreted as `documentType` by the explicit-target API.

Vespa stores decimal model properties as doubles. Comparisons on those fields follow that stored precision. Comparisons that promote an integer document field to decimal, such as `p.Quantity > 0m`, are rejected because native evaluation could round a decimal threshold and change the result; use an integer threshold instead. Evaluate decimal comparisons between captured values in C# and capture the resulting boolean.

To reuse a typed predicate in a feed operation or another API that accepts a string, translate it explicitly:

```csharp
using Vespa.Feed;

var condition = DocumentCondition.For<Product>(p => p.Price > 0);
// "(product.price > 0)"

var operation = FeedOperation.Put(product.Id, product, condition: condition);
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
| `FieldOp.Modify(op, cells, create)` | Tensor cell-level update (`create: true` treats an unset tensor as empty) |
| `FieldOp.ModifyCells(op, cells, create)` | Tensor modify, short-form `cells` object (single mapped dimension) |
| `FieldOp.ModifyBlocks(op, blocks, create)` | Tensor modify, `blocks` form (mixed tensors) |
| `FieldOp.TensorAdd(cells)` | Add cells to a mapped/mixed tensor (`{"add":{"cells":[…]}}`) |
| `FieldOp.TensorRemove(addresses)` | Remove cells/blocks by address (`{"remove":{"addresses":[…]}}`) |

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

// Model-aware writes and deletes infer schema and namespace from [VespaDocument]
await client.Documents.PutByGroupAsync("tenant-42", product.Id, product);
await client.Documents.PutByNumberAsync(42L, product.Id, product);
await client.Documents.DeleteByGroupAsync<Product>("tenant-42", product.Id);
await client.Documents.DeleteByNumberAsync<Product>(42L, product.Id);
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

> [!NOTE]
> Vespa answers selection operations with a plain JSON body
> (`{"documentCount": N, "continuation": "…"}`); `stream=true` only applies to
> visits, so `SelectionRequestOptions` deliberately has no `Stream` flag.
> Use a larger `TimeChunk` to reduce the number of round-trips on huge selections.

### Manual Pagination (Crash-Safe Resume)

Each selection operation has a `...PageAsync` variant that performs exactly one
HTTP call, returns a `SelectionPageResult`, and lets you drive the loop yourself.
Use this when you need to persist the continuation token between chunks — for
example, to resume a long-running backfill after a crash or deployment.

`SelectionPageResult` exposes:

- `DocumentCount` — documents affected **in this chunk only** (not cumulative).
- `Continuation` — token to pass back to resume; `null` when complete.
- `IsComplete` — `true` iff `Continuation is null`.
- `StatusCode`, `HadIgnoredFields` — same as `VespaResponse` (`HadIgnoredFields` is a
  boolean flag from the `X-Vespa-Ignored-Fields: true` header; Vespa does not report
  which fields were ignored).

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

Common parameters (`selection`, `cluster`, `namespace`) stay on the method; everything
else lives in the `VisitOptions` record:

```csharp
await foreach (var doc in client.Documents.VisitAsync<Product>(
    documentType: "product",
    selection: "product.price > 100",
    options: new VisitOptions
    {
        WantedDocumentCount = 1000,
        Slices = 4, SliceId = 0,        // parallel slicing
        FromTimestamp = 1700000000000000, // microseconds since UTC epoch
        IncludeRemoves = true
    }))
{
    Process(doc.Fields);
}
```

`VisitOptions` also supports `FieldSet`, `Timeout`, `Concurrency`, `ToTimestamp`,
`BucketSpace`, `TraceLevel`, and `TensorFormat`. `FromTimestamp`/`ToTimestamp` are
**microseconds** since UTC epoch. `dryRun` is deliberately unavailable — Vespa
rejects it on visits.

### Group / Number / Root-Level Visiting

```csharp
// Visit one group (streaming-mode document types)
await foreach (var doc in client.Documents.VisitAsync<Product>(
    "product", options: new VisitOptions { Group = "tenant-42" })) { }

// Visit one numeric bucket
await foreach (var doc in client.Documents.VisitAsync<Product>(
    "product", options: new VisitOptions { Number = 42L })) { }

// Visit ALL documents in a cluster, across namespaces and document types
await foreach (var doc in client.Documents.VisitAllAsync<Dictionary<string, object>>(
    cluster: "content")) { }
```

### Manual Visit Pages and Checkpoints

`VisitPageAsync<T>` makes one request and returns a `VespaVisitResponse<T>` with
`Documents`, `DocumentCount`, `PathId`, `Continuation` and `IsComplete`. It does
not fetch the next page automatically. The model-aware overload infers the schema
and namespace from `[VespaDocument]`:

```csharp
var (continuation, completed) = await LoadCheckpointAsync();
var options = new VisitOptions { WantedDocumentCount = 500, Concurrency = 2 };

while (!completed)
{
    var page = await client.Documents.VisitPageAsync<Product>(
        selection: "product.price > 100",
        cluster: "content",
        continuation: continuation,
        options: options,
        cancellationToken: cancellationToken);

    await ProcessPageAsync(page.Documents, cancellationToken);
    continuation = page.Continuation;
    completed = page.IsComplete;
    await SaveCheckpointAsync(continuation, completed);
}
```

Save the continuation **after** processing the page. Treat it as an opaque string
and resume with the same schema, namespace, selection, cluster and visit options
(including slice settings). Persist completion separately: a null continuation
also represents the initial request. Reprocessing a page after a crash is possible.

An empty page can still have a continuation; only `IsComplete` signals completion.
`WantedDocumentCount` is a best-effort lower bound, not a maximum page size.
Document IDs and `[VespaId]` injection work as with `VisitAsync`.

For explicit schema/namespace addressing, pass `documentType` and `namespace`.
Use `VisitAllPageAsync<T>(cluster: "content", continuation: continuation)` to page
across all schemas and namespaces in a cluster. It requires a cluster and does not
accept `VisitOptions.Group` or `VisitOptions.Number`.

### JSONL Streaming (lower memory)

`VisitJsonlAsync` streams one document per line (`stream=true`,
`Accept: application/jsonl`) and takes the same `VisitOptions`. Prefer it for large
result sets. Notes:

- Requires **Vespa ≥ 8.593** — older versions answer with plain JSON, which the
  client rejects with a `VespaException` instead of silently yielding nothing.
- If a chunk ends with a continuation token (e.g. after a server-side visit
  timeout), the client **resumes automatically** until the final continuation
  reports completion. Malformed lines or a missing final continuation marker
  throw `VespaException`; an incomplete export is never reported as completed.
- Server messages with severity `error` throw `VespaException`. Messages with
  severity `info` or `warning` are logged at the corresponding level and the
  visit continues.
- `FullId` retains the complete Vespa document ID, including group/number
  addressing, for both documents and removal tombstones. `Id` is the short user part.
- `WantedDocumentCount` is not sent on this path — streaming makes it redundant.

```csharp
await foreach (var doc in client.Documents.VisitJsonlAsync<Product>(
    documentType: "product",
    selection: "product.price > 100",
    options: new VisitOptions
    {
        Slices = 8, SliceId = 0,
        Concurrency = 4,
        Timeout = TimeSpan.FromMinutes(2),
        BucketSpace = "default"
    }))
{
    Process(doc.Fields);
}
```

Selection-based update/delete/copy operations also require a readable response
with `documentCount`. A malformed or interrupted response throws instead of
treating a missing continuation as successful completion. The server may already
have applied part of the operation; the client does not automatically repeat it.

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
    Cluster      = "content",                  // forwarded on GETs only
};

await client.Documents.PutAsync(product.Id, product, documentType: "product", requestOptions: opts);
```

Model-aware writes accept `requestOptions:` after the document arguments. Model-aware reads accept the options object before the document ID. Cancellation tokens remain the final parameter:

```csharp
var readOptions = new DocumentRequestOptions
{
    Cluster = "content",
    Timeout = TimeSpan.FromSeconds(10),
    TensorFormat = "long"
};
var writeOptions = new DocumentRequestOptions { Timeout = TimeSpan.FromSeconds(10) };

var doc = await client.Documents.GetAsync<Product>(readOptions, product.Id);
await client.Documents.PutAsync(product.Id, product, requestOptions: writeOptions);
await client.Documents.UpdateFieldsAsync<Product>(product.Id,
    update => update.Field(p => p.Price, FieldOp.Assign(49.99m)), requestOptions: writeOptions);
await client.Documents.DeleteAsync<Product>(product.Id, requestOptions: writeOptions);

// Addressed reads and GetManyAsync also accept options before the ID.
var groupDoc = await client.Documents.GetByGroupAsync<Product>(readOptions, "tenant-42", product.Id);
var docs = await client.Documents.GetManyAsync<Product>(readOptions, ["p-1", "p-2"]);
```

All model-aware group and number writes accept `requestOptions:` as a named argument. `Cluster` is forwarded only on reads; write routing uses `Route`.

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
