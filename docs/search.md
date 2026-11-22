# Search

Full reference for Vespa.NET's search capabilities: basic search, nearest-neighbor, hybrid search, streaming, and request options.

For an Agent Framework example that combines text and vector search, see the [retrieval guide](native-retrieval.md). The [evaluation guide](retrieval-quality.md) shows how to compare rankings against relevance judgments.

---

## Basic Search

```csharp
var request = new VespaSearchRequest
{
    Yql = "select * from product where true;",
    Hits = 20
};

var response = await client.Search.SearchAsync<Product>(request);
foreach (var hit in response.Root.Children)
    Console.WriteLine($"{hit.Fields?.Name}: {hit.Relevance:F3}");
```

HTTP failures preserve the primary server error from `root.errors` in `VespaException.Error`. Successful responses can still contain backend errors or degraded coverage: inspect `response.Root.Errors` and `response.Root.Coverage`.

## Tensor Response Formats

`PresentationFormatTensors = "hex-value"` omits tensor type information from the response. Declare `[VespaTensor("tensor<float>(x[384])")]` (using the actual schema type) on a `VespaTensor` property so the client can decode the correct cell width. The same metadata disambiguates mapped labels named `type`, `cells`, `blocks`, or `values` in value-only forms.

When using `VespaTensorConverter` directly, supply the type explicitly:

```csharp
var jsonOptions = new JsonSerializerOptions
{
    Converters = { new VespaTensorConverter("tensor<float>(x[2])") }
};
var tensor = JsonSerializer.Deserialize<VespaTensor>("\"3F80000040000000\"", jsonOptions);
```

Hex data without a type is rejected with `JsonException` rather than decoded by guessing. Use the self-describing `hex` format when schema metadata is unavailable.

---

## Nearest-Neighbor Search

`NearestNeighborSearchAsync` generates `{totalTargetHits: topK}nearestNeighbor(...)`. The `filter` string is embedded verbatim in the YQL — never pass unvalidated end-user input; build it with `YqlBuilder` where possible.

```csharp
// Lambda — resolves field name via [VespaTensor]
var results = await client.Search.NearestNeighborSearchAsync<Product>(
    queryEmbedding, p => p.Embedding, topK: 10);

// Explicit field name with filter
var results2 = await client.Search.NearestNeighborSearchAsync<Product>(
    queryEmbedding,
    embeddingField: "embedding",
    topK: 10,
    filter: "price < 500",
    rankProfile: "semantic");

// Via YQL builder with annotations (totalTargetHits preferred over per-node targetHits)
var yql = YqlBuilder<Product>
    .Select()
    .Where(w => w.NearestNeighbor(
        p => p.Embedding, "query_embedding",
        totalTargetHits: 100,
        approximate: true,
        distanceThreshold: 0.5,
        hnswExploreAdditionalHits: 200))
    .Build();
```

---

## Hybrid Search (Text + Vector)

`HybridSearch` generates the standard Vespa `rank()` pattern — nearest-neighbor retrieves documents, `userInput` contributes text-matching features for ranking:

```csharp
var yql = YqlBuilder<Product>
    .Select()
    .Where(w => w.HybridSearch(
        p => p.Embedding, "query_embedding", "userQuery", targetHits: 100))
    .Build();
// select * from product where rank({targetHits: 100}nearestNeighbor(embedding, query_embedding), userInput(@userQuery));

// Combined with filters
var yql2 = YqlBuilder<Product>
    .Select()
    .Where(w =>
    {
        w.Field(p => p.Price).LessThan(500);
        w.HybridSearch(p => p.Embedding, "query_embedding", "userQuery", targetHits: 100);
    })
    .Build();
```

---

## Auto-Paginated Search

`SearchStreamAsync<T>` returns an `IAsyncEnumerable<SearchHit<T>>` that auto-paginates:

```csharp
var request = new VespaSearchRequest { Yql = "select * from product where true;" };

await foreach (var hit in client.Search.SearchStreamAsync<Product>(request, pageSize: 100))
    Console.WriteLine($"{hit.Fields?.Name}: {hit.Relevance:F3}");
```

## Paged Search

`SearchPagedAsync<T>` yields full response pages:

```csharp
await foreach (var page in client.Search.SearchPagedAsync<Product>(request, pageSize: 50))
    Console.WriteLine($"Page with {page.Root.Children.Count} hits");
```

> **Server limits:** Vespa caps `hits` at `maxHits` (default 400) and `offset` at `maxOffset` (default 1000). Deep pagination fails unless those query-profile limits are raised. To export a full corpus, use document visiting (`Documents.VisitAsync` / `VisitJsonlAsync`) instead of search pagination.

## Native Streaming Search

Configure the document type for `streaming` mode in the content cluster. To search one group, feed its documents with group-addressed IDs such as `id:catalog:product:g=tenant-a:123`. This is separate from `SearchStreamAsync`, which paginates search results. Select the group and put the content filter in YQL:

```csharp
using Vespa.Query;

var request = YqlBuilder.Select().From("product")
    .Where(w => w.Field("price").GreaterThan(100))
    .ToSearchRequest();
request.StreamingGroupName = "tenant-a";

var results = await client.Search.SearchAsync<Product>(request);
```

For several groups, use `WithStreamingGroups` on a request without `StreamingGroupName`:

```csharp
var request = YqlBuilder.Select().From("product")
    .Where(w => w.Field("price").GreaterThan(100))
    .ToSearchRequest()
    .WithStreamingGroups("tenant-a", "tenant-b");
```

This produces `streaming.selection` as `id.group == "tenant-a" or id.group == "tenant-b"`. Vespa can restrict visiting to those groups, then apply YQL to the selected documents. Other selector forms can scan all groups, so prefer group selection with a YQL filter for searches within known groups. See the [Vespa streaming query reference](https://docs.vespa.ai/en/reference/api/query.html#streaming).

`WithStreamingGroups` freezes the names, escapes each literal and removes ordinal duplicates. Names that differ in case remain distinct. It rejects empty input, blank names, malformed Unicode and invalid Vespa text characters. A group ending in a backslash is placed last to avoid a Vespa selector-parser limitation; combining more than one distinct name ending in a backslash throws `NotSupportedException`. Use separate requests for those groups.

For a document-field selector, use a typed predicate:

```csharp
var minimumPrice = 100;
var request = new VespaSearchRequest
{
    Yql = "select * from product where true"
}.WithStreamingSelection<Product>(p => p.Price > minimumPrice);
```

This uses the model's document type and mapped fields and restricts the entire predicate to that exact document type. It can scan all groups. The filter model does not choose the YQL source, namespace or result type; `SearchAsync<Summary>` can return a separate summary model. A document selector is not a namespace or authorization boundary.

Both helpers set only `StreamingSelection`, return the same request and freeze captured values when called. They replace an existing selection. A null predicate or unsupported expression throws before replacement. An existing `StreamingGroupName`, or `streaming.selection`, `streaming.groupname` or `streaming.userid` in `CustomParameters`, causes `InvalidOperationException`; clear the conflicting property first. Later changes made through raw properties are the caller's responsibility.

The native string property remains available, including for models without `[VespaDocument]`:

```csharp
using Vespa.Documents;

request.StreamingSelection = DocumentSelection.For<PlainProduct>(
    "product", p => p.Price > minimumPrice);
```

`StreamingMaxBucketsPerVisit` maps to `streaming.maxbucketspervisitor` and controls backend bucket concurrency. It does not limit the total buckets visited or prevent a scan across all groups. The public property name and wire format are unchanged.

---

## Search Request Options

```csharp
var request = new VespaSearchRequest
{
    Yql = yql,
    Hits = 10,
    Ranking = new RankingConfig { Profile = "semantic" },
    Input = new() { ["query(embedding)"] = queryTensor },

    // Collapse / deduplication
    CollapseField = "product_id",
    CollapseSize = 1,

    // Presentation
    PresentationBolding = true,
    PresentationSummary = "compact",

    // Model parameters
    ModelRestrict = "product",
    ModelSources = "content_cluster",

    // Trace / diagnostics
    TraceLevel = 3,
    TraceTimestamps = true,

    // Custom parameters (for userInput(@param))
    CustomParameters = new() { ["text"] = "search query" }
};

// Fluent helpers
request.WithTimeout(TimeSpan.FromSeconds(5))
       .WithCollapse("category", size: 2);
```
