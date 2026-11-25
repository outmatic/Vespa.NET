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

## Document Field Sets

Use `DocumentFieldSet` to choose fields for a document GET or visit. It resolves `[VespaField(Name = "...")]` and snake_case names from direct model properties:

```csharp
using Vespa.Documents;

var fields = DocumentFieldSet.For<Product>(p => p.Name, p => p.Price);
// "product:product_name,price" when Name maps to "product_name"

var doc = await client.Documents.GetAsync<Product>(product.Id, fieldSet: fields);
```

On model-aware GET calls that start with the ID or address, pass **`fieldSet:` by name**. A positional string in that slot is interpreted as the native `documentType` argument. This applies to `GetAsync`, `GetByGroupAsync` and `GetByNumberAsync`. The options-first read overloads accept the field set either positionally or by name.

Reuse the string in `VisitOptions` for automatic visits, manual pages or JSONL:

```csharp
var options = new VisitOptions { FieldSet = fields };

await foreach (var doc in client.Documents.VisitAsync<Product>(
    selection: p => p.Category == "electronics", options: options))
    Console.WriteLine($"{doc.Fields?.Name}: {doc.Fields?.Price}");
```

The selection decides which documents match; the field set chooses fields to fetch. A field used in the selection does not have to appear in the field set. Keep the same options when resuming a manual page with its continuation token.

For a model without `[VespaDocument]`, specify the document type explicitly:

```csharp
var fields = DocumentFieldSet.ForDocumentType<PlainProduct>(
    "product", p => p.Price);

var doc = await client.Documents.GetAsync<PlainProduct>(product.Id,
    documentType: "product", @namespace: "catalog", fieldSet: fields);
```

`ForDocumentType` uses the supplied type even if the model has a different annotation. Field names still come from the selected properties. The qualifier does not set the request's target, namespace or document selection, so keep it consistent with the document type being read. A separate result model may be used on native reads or root visits.

Both factories return a string and require at least one field. They preserve the first occurrence of each mapped name, comparing names ordinally, and retain no reference to the selector array. They validate schema identifier syntax locally; Vespa checks whether the document type and fields exist in the deployed application.

Select a whole complex property such as `p => p.Address` to request that field. Nested paths such as `p => p.Address.City`, indexers, methods, document IDs and properties without an individual document JSON binding are rejected. To fetch identity alone, pass the native `[id]` field set. The factory uses the same binding checks as typed conditions and updates.

Existing native strings remain available through `fieldSet:` and `VisitOptions.FieldSet`:

| Field set | Fields requested |
| --- | --- |
| `[document]` | Original document fields and the document ID; this is the server default. |
| `[all]` | All schema fields, including generated fields, and the document ID. |
| `[id]` | The document ID. |
| `product:product_name,price` | Named fields from the specified document type. |

Use `[document]` instead of the deprecated `product:[document]` form. Vespa may return additional fields when omitting them offers no performance benefit. An unrequested property in a deserialized C# model can retain its default or initializer value. See [Vespa document field sets](https://docs.vespa.ai/en/schemas/documents.html#fieldsets).

Document field sets apply to reads through `/document/v1/`. Searchable fieldsets declared with `[VespaFieldSet]` group fields for matching, and YQL `SELECT` chooses search result fields; those are separate features.

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

Enum properties use their numeric value when the backing numeric type is supported. Use `rawCondition:` to compare values of `DateTime`, `TimeSpan`, `Guid`, `char`, collections or tensors, and for enum properties with a JSON converter on the property or enum type. Typed collection null checks such as `p => p.Tags != null` remain supported. A converter can change the stored representation, so typed enum comparisons reject it.

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

Feed operation factories accept typed predicates directly. To reuse a rendered native condition in a feed operation, pass it through `rawCondition:`:

```csharp
using Vespa.Feed;

var condition = DocumentCondition.For<Product>(p => p.Price > 0);
// "(product.price > 0)"

var operation = FeedOperation.Put(product.Id, product, rawCondition: condition);
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
| `FieldOp.Modify(op, cells, create)` | Tensor cell-level update |
| `FieldOp.ModifyCells(op, cells, create)` | Tensor modify, short-form `cells` object (single mapped dimension) |
| `FieldOp.ModifyBlocks(op, blocks, create)` | Tensor modify, `blocks` form (mixed tensors) |
| `FieldOp.TensorAdd(cells)` | Add cells to a mapped/mixed tensor (`{"add":{"cells":[…]}}`) |
| `FieldOp.TensorRemove(addresses)` | Remove cells/blocks by address (`{"remove":{"addresses":[…]}}`) |

For tensor `modify`, set `create: true` to create missing cells. Vespa supports this flag from 8.217.46 for an existing tensor and from 8.218.31 when the tensor field is unset. Newly created mapped cells start at zero for `replace` and `add`, and at one for `multiply`. [Document JSON](https://docs.vespa.ai/en/reference/schemas/document-json-format.html#modify-tensors) · [Vespa operation defaults](https://github.com/vespa-engine/vespa/blob/v8.217.46/document/src/main/java/com/yahoo/document/update/TensorModifyUpdate.java#L60)

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

In 4.0, the model-aware update, delete and copy methods accept `selection:` for a lambda or `rawSelection:` for a native selector. Each has an automatic form and a single-page form. Updates accept either a field-operation dictionary or the typed field builder:

```csharp
await client.Documents.UpdateBySelectionAsync<Product>(
    update => update.Field(p => p.Price, FieldOp.Multiply(0.9)),
    selection: p => p.Price > 100,
    cluster: "content");

await client.Documents.DeleteBySelectionAsync<Product>(
    selection: p => p.Price <= 0,
    cluster: "content");

await client.Documents.CopyBySelectionAsync<Product>(
    cluster: "content",
    destinationCluster: "archive",
    selection: p => p.Price > 100);
```

These methods infer the document type and namespace from `[VespaDocument]`. Keep `<Product>` on calls using dictionaries or native strings, since those arguments do not identify the model. Use named cluster arguments for copies to distinguish source from destination; copying leaves the source documents in place.

Exactly one selection is required. Missing selections, conflicting non-null arguments and blank native strings throw `ArgumentException` before an update callback runs or an HTTP request is sent. To select every document of the model's exact type in its resolved namespace, pass `selection: p => true`. Passing `rawSelection: "true"` instead sends native `true` to the addressed operation without adding an exact-type guard. A typed selection is frozen before the update callback, so that callback cannot change the filter by modifying a captured value.

For a native selector, write `UpdateBySelectionAsync<Product>(fields, rawSelection: "product.price < 10", cluster: "content")`. Nonblank native strings are forwarded without rewriting. The low-level methods with an explicit `documentType` retain their existing string `selection` argument, as in the examples below.

Selection operations apply a Vespa selection expression across an entire document type. Vespa processes them in time-bounded chunks and returns a `continuation` token while more work remains. Vespa.NET follows those tokens and sums the number of documents visited and selected in `VespaResponse.DocumentCount`.

A selection update can return before the individual writes are visible to `GetAsync`. If a subsequent step needs to observe the change, verify the affected documents with a bounded read retry. [Vespa Document API](https://docs.vespa.ai/en/reference/api/document-v1.html#response-formats)

```csharp
// Update all matching documents (loops internally until complete)
var result = await client.Documents.UpdateBySelectionAsync(
    selection: "product.price < 10",
    fieldOperations: new() { ["price"] = FieldOp.Assign(10.0) },
    documentType: "product",
    cluster: "content");

Console.WriteLine($"Matched {result.DocumentCount} documents");

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

The model-aware `UpdateBySelectionPageAsync<Product>`, `DeleteBySelectionPageAsync<Product>` and `CopyBySelectionPageAsync<Product>` accept the same typed or native selectors. For manual resume, render `DocumentSelection.For<Product>(...)` before starting, save that string with the continuation token and pass it as `rawSelection:` on each call. Calling a lambda again reads its captured values again. The automatic methods freeze one selector for their entire continuation loop.

Each selection operation has a `...PageAsync` variant that performs exactly one
HTTP call, returns a `SelectionPageResult`, and lets you drive the loop yourself.
Use this when you need to persist the continuation token between chunks — for
example, to resume a long-running backfill after a crash or deployment.

`SelectionPageResult` exposes:

- `DocumentCount` — documents visited and selected by Vespa in this chunk.
- `Continuation` — token to pass back to resume; `null` when complete.
- `IsComplete` — `true` iff `Continuation is null`.
- `StatusCode`, `HadIgnoredFields` — same as `VespaResponse` (`HadIgnoredFields` is a
  boolean flag from the `X-Vespa-Ignored-Fields: true` header; Vespa does not report
  which fields were ignored).

```csharp
string? token = await LoadCheckpoint();  // e.g. from database, null on first run
long totalMatched = 0;

while (true)
{
    var page = await client.Documents.UpdateBySelectionPageAsync(
        selection: "product.price < 10",
        fieldOperations: new() { ["price"] = FieldOp.Assign(10.0) },
        documentType: "product",
        cluster: "content",
        continuation: token);

    totalMatched += page.DocumentCount;

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

### Typed Selections

In 4.0, model-aware `VisitAsync`, `VisitPageAsync` and `VisitJsonlAsync` accept a lambda through `selection:` or a native selector through `rawSelection:`. They infer the document type and namespace from `[VespaDocument]`:

```csharp
await foreach (var doc in client.Documents.VisitAsync<Product>(
    selection: p => p.Price > 100,
    cluster: "content"))
    Process(doc.Fields);

await foreach (var doc in client.Documents.VisitJsonlAsync<Product>(
    rawSelection: "product.price > 100",
    cluster: "content"))
    Process(doc.Fields);
```

Omit both selections to visit the addressed scope. Passing non-null values for both throws `ArgumentException` when the method is called, before enumeration or HTTP. A typed predicate is translated when the method is called, so changes to captured values after that call do not change the filter between pages or JSONL chunks.

`DocumentSelection.For<Product>(p => p.Price > 100)` produces a native selector you can save or pass to other Document API methods. It scopes the entire predicate to the exact document type, including `p => true` and negations. This differs from `DocumentCondition.For`, which renders a write condition without adding a type filter. See the [document selector reference](https://docs.vespa.ai/en/reference/writing/document-selector-language.html).

For an explicit target, use `DocumentSelection.For<Product>("other_product", p => p.Price > 100)` and pass the same document type to the native operation. This factory also accepts models without `[VespaDocument]`. The field mapping still comes from the selected properties.

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

For a typed root filter, specify the result model first and the filter model second. The result model needs no `[VespaDocument]` attribute:

```csharp
await foreach (var doc in client.Documents.VisitAllAsync<ProductSummary, Product>(
    cluster: "content",
    selection: p => !(p.Price < 100)))
    Process(doc.Fields);
```

This visits only the exact Product document type across namespaces. Derived document types are excluded. `VisitAllPageAsync<ProductSummary, Product>` provides the same filtering for manual pages. Existing root methods with one generic argument retain their unrestricted native selection; use `DocumentSelection.For<Product>(...)` as their `selection:` argument when you want a saved typed filter. Root visits still require a cluster and do not support group or number addressing.

### Manual Visit Pages and Checkpoints

`VisitPageAsync<T>` makes one request and returns a `VespaVisitResponse<T>` with
`Documents`, `DocumentCount`, `PathId`, `Continuation` and `IsComplete`. It does
not fetch the next page automatically. The model-aware overload infers the schema
and namespace from `[VespaDocument]`:

```csharp
var (continuation, completed, selection) = await LoadCheckpointAsync();
selection ??= DocumentSelection.For<Product>(p => p.Price > 100);
var options = new VisitOptions { WantedDocumentCount = 500, Concurrency = 2 };

while (!completed)
{
    var page = await client.Documents.VisitPageAsync<Product>(
        rawSelection: selection,
        cluster: "content",
        continuation: continuation,
        options: options,
        cancellationToken: cancellationToken);

    await ProcessPageAsync(page.Documents, cancellationToken);
    continuation = page.Continuation;
    completed = page.IsComplete;
    await SaveCheckpointAsync(continuation, completed, selection);
}
```

Save the continuation **after** processing the page. Treat it as an opaque string
and resume with the same schema, namespace, selection, cluster and visit options
(including slice settings). Persist completion separately: a null continuation
also represents the initial request. Reprocessing a page after a crash is possible.

The example saves the rendered selection with the checkpoint. Reusing that string keeps the filter stable even if the original captured values change before a restart. A new call with a lambda reads its captured values again.

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
