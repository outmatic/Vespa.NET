# Search

Full reference for Vespa.NET's search capabilities: basic search, nearest-neighbor, hybrid search, streaming, and request options.

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

---

## Nearest-Neighbor Search

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

// Via YQL builder with annotations
var yql = YqlBuilder<Product>
    .Select()
    .Where(w => w.NearestNeighbor(
        p => p.Embedding, "query_embedding",
        targetHits: 100,
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

## Streaming Search (Auto-Paginated)

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
