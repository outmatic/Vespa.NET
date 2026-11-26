# YQL Builder

Vespa.NET provides a fluent, type-safe YQL query builder. Two entry points are available:

- **`YqlBuilder<T>`** (recommended) — infers document type from `[VespaDocument]`, resolves field names via lambdas
- **`YqlBuilder`** — untyped, string-based field names

---

## Typed Builder

```csharp
using Vespa.Query;

// All fields, typed where clause
var yql = YqlBuilder<Product>
    .Select()
    .Where(w => w.Field(p => p.Price).LessThan(500))
    .Limit(20)
    .Build();
// select * from product where price < 500 limit 20

// Specific fields — names resolved via [VespaField]
var yql2 = YqlBuilder<Product>
    .Select(p => p.Name, p => p.Price)
    .Where(w => w.Field(p => p.Price).GreaterThan(10)
                 .And(sub => sub.Field(p => p.Name).Contains("laptop")))
    .OrderBy(p => p.Price, SortDirection.Descending)
    .Build();
```

## Untyped Builder

```csharp
var yql = YqlBuilder
    .Select("product_name, price")
    .From("product")
    .Where(w => w
        .Field("price").GreaterThan(10)
        .And(sub => sub.Field("category").Contains("electronics")))
    .OrderBy("price", SortDirection.Descending)
    .Build();
```

---

## Field Predicates

| Method | YQL Output |
|---|---|
| `.Contains("text")` | `field contains "text"` |
| `.ContainsAnnotated("text", new() { Filter = true })` | `field contains ({filter: true}"text")` |
| `.Text("value")` | `field contains text("value")` |
| `.Matches("regex.*")` | `field matches "regex.*"` |
| `.EqualTo(val)` | `field = val` |
| `.GreaterThan(n)` / `.LessThan(n)` | `field > n` / `field < n` |
| `.GreaterOrEqual(n)` / `.LessOrEqual(n)` | `field >= n` / `field <= n` |
| `.Range(from, to)` | `range(field, from, to)` |
| `.In("a", "b")` | `field in ("a", "b")` — throws on zero values |
| `.InParameter("param")` | `field in (@param)` — values sent as a request parameter |
| `.Phrase("w1", "w2")` | `field contains phrase("w1", "w2")` |
| `.Fuzzy("term", maxEditDistance: 2)` | `field contains ({maxEditDistance:2}fuzzy("term"))` |
| `.Near(["t1", "t2"], distance: 3)` | `field contains ({distance: 3}near("t1", "t2"))` |
| `.Near(["t1"], excludedTerms: ["t2"])` | `field contains near("t1", !"t2")` |
| `.ONear(["t1", "t2"])` | `field contains onear("t1", "t2")` |
| `.Equiv("t1", "t2")` | `field contains equiv("t1", "t2")` |
| `.SameElement(...)` | `field contains sameElement(...)` — `elementFilter: [0, 2]` adds `{elementFilter:[0, 2]}` |
| `.Uri("vespa.ai")` | `field contains (uri("vespa.ai"))` (deprecated upstream) |

Pass a `YqlTermAnnotations` object to set term weight, ranking, matching, or linguistic options. Omitted properties use Vespa's defaults. Set `TermId` to identify the current term and `Connectivity` to give the target term's ID and a proximity weight. [Vespa term annotations](https://docs.vespa.ai/en/reference/querying/yql.html#annotations)

```csharp
var query = YqlBuilder<Product>.Select().Where(w =>
{
    var name = w.Field(p => p.Name);
    name.ContainsAnnotated("wireless", new() { TermId = 1, Connectivity = new(2, 0.8) });
    name.ContainsAnnotated("headphones", new() { TermId = 2 });
}).Build();
```

### URL component matching

`Uri(...)` queries an existing indexed Vespa `uri` field or one of its URL components. If the field is named `url`, `w.Field("url.hostname").Uri("vespa.ai")` matches both `vespa.ai` and `docs.vespa.ai`. With the default end anchor, adding `startAnchor: true` restricts the match to `vespa.ai`. [Vespa hostname anchors](https://docs.vespa.ai/en/reference/querying/yql.html#endanchor)

Vespa recommends `string` for new schemas. If an application needs exact hostname lookup, store the normalized hostname in a dedicated field and query that field using exact matching. [Vespa URI fields](https://docs.vespa.ai/en/reference/schemas/schemas.html#uri)

## Clause-Level Predicates

Vespa added `totalTargetHits` in different releases for each operator: 8.639.59 for `nearestNeighbor`, 8.640.27 for `weakAnd` and 8.664.22 for `wand`. Use `targetHits` for a per-node target. [Nearest-neighbor support](https://github.com/vespa-engine/vespa/pull/35803) · [WeakAnd support](https://github.com/vespa-engine/vespa/pull/35815) · [Wand support](https://github.com/vespa-engine/vespa/pull/36233)

| Method | YQL Output |
|---|---|
| `w.NearestNeighbor("emb", "q", 10)` | `{targetHits:10}nearestNeighbor(emb, q)` |
| `w.NearestNeighbor("emb", "q", totalTargetHits: 100)` | `{totalTargetHits:100}nearestNeighbor(emb, q)` (preferred; `minTargetHits` also supported) |
| `w.WeakAnd(s => ..., totalTargetHits: 100)` | `{totalTargetHits:100}weakAnd(...)` |
| `w.Wand("f", terms, targetHits: 100)` | `{targetHits:100}wand(f, {...})` — `totalTargetHits` supported |
| `w.DotProduct("f", terms)` | `dotProduct(f, {...})` |
| `w.WeightedSet("f", tokens)` | `weightedSet(f, {...})` |
| `w.GeoLocation("pos", lat, lon, 10.0)` | `geoLocation(pos, lat, lon, "10km")` |
| `w.GeoBoundingBox("pos", s, w, n, e)` | `geoBoundingBox(pos, s, w, n, e)` |
| `w.UserQuery("query expression")` | `userQuery()` reads `model.queryString`, set by `ToSearchRequest()` or `WithYql()` |
| `w.UserQuery()` | `userQuery()` reads the [model query set on the request](search.md#user-text-and-model-filters) |
| `w.UserInput("param")` | `userInput(@param)` |
| `w.Rank(...)` | `rank(...)` |
| `w.Not(n => ...)` | `!( ... )` |
| `w.NonEmpty(...)` | `nonEmpty(...)` |
| `w.Or(or => { or.A(); or.B(); })` | `A or B` |
| `w.AnyOf(p => p.A(), p => p.B())` | `A or B` |
| `w.HybridSearch("emb", "q", "param", 100)` | `rank({targetHits:100}nearestNeighbor(emb, q), userInput(@param))` |
| `w.Predicate("f", attrs, ranges)` | `predicate(f, {...}, {...})` — empty maps emit `0` |
| `w.Raw("...")` | embedded verbatim — **unvalidated**, YQL-injection risk with user input |

---

## Boolean Composition

Predicates added directly to the `Where` callback are **AND'd** together. Use `Or` or `AnyOf` for disjunctions.

### AND (implicit)

```csharp
.Where(w =>
{
    w.Field(p => p.Category).Contains("electronics");
    w.Field(p => p.Price).LessThan(500);
})
// category contains "electronics" and price < 500
```

### OR callback

All predicates inside the `Or` callback are **OR'd**:

```csharp
.Where(w => w.Field(p => p.Status).Contains("active")
    .Or(or =>
    {
        or.Field(p => p.Pinned).EqualTo(1);
        or.Field(p => p.ExpiresAt).EqualTo(0);
        or.Field(p => p.ExpiresAt).GreaterThan(now);
    }))
// status contains "active" or pinned = 1 or expires_at = 0 or expires_at > 1000
```

### AnyOf

Each callback builds one predicate, all are OR'd:

```csharp
.Where(w => w.And(a => a.AnyOf(
    p => p.Field(d => d.Pinned).EqualTo(1),
    p => p.Field(d => d.ExpiresAt).EqualTo(0),
    p => p.Field(d => d.ExpiresAt).GreaterThan(now))))
```

### Combining AND + OR

```csharp
.Where(w =>
{
    w.Field(p => p.Scope).Contains("Session");
    w.Field(p => p.ScopeId).Contains("abc");
    w.And(a => a.Or(or =>
    {
        or.Field(p => p.Pinned).EqualTo(1);
        or.Field(p => p.ExpiresAt).EqualTo(0);
        or.Field(p => p.ExpiresAt).GreaterThan(now);
    }));
})
// scope contains "Session" and scope_id contains "abc"
//   and (pinned = 1 or expires_at = 0 or expires_at > 1000)
```

---

## Fluent Request Builder

`ToSearchRequest()` creates a request from the builder. Use `request.WithYql(builder)` to apply the builder to an existing request. Chain helpers for ranking and tensors:

```csharp
var request = YqlBuilder<Product>
    .Select()
    .Where(w => w.HybridSearch(p => p.Embedding, "q", "userQuery", targetHits: 100))
    .Limit(20)
    .ToSearchRequest()
    .WithRankProfile("hybrid_twophase")
    .WithQueryTensor("q", "embed(e5small, @userQuery)")
    .WithUserInput("userQuery", searchText);
```

| Method | What It Sets |
|---|---|
| `.WithRankProfile("name")` | `Ranking.Profile` |
| `.WithQueryTensor("name", value)` | `Input["query(name)"]` |
| `.WithUserInput("param", value)` | `CustomParameters["param"]` |
| `.WithRankFeature("name", value)` | `Ranking.Features["query(name)"]` |
| `.WithTimeout(TimeSpan)` | `Timeout` |
| `.WithCollapse("field", size)` | `CollapseField` + `CollapseSize` |

---

## Validation

The builder validates at build time:

| Method | Requirement |
|---|---|
| `AnyOf(...)` | At least 2 predicates |
| `Rank(...)` | At least 2 operands |
| `WeakAnd(...)` | At least 1 predicate |
| `HybridSearch(...)` | Non-empty field, tensor, and parameter names |
| `Field(...)`, `OrderBy(...)`, `Select(...)` | Names must match the YQL identifier grammar (dot-separated `[A-Za-z_][A-Za-z0-9_]*` segments, optional `field{"key"}` map suffix) — use `OrderByRaw(...)` for annotated order-by expressions |
| `EqualTo(...)` | Numeric/boolean only — YQL's `=` rejects strings; use `Contains(...)` or `In(...)` |
| Literals | `bool` → lowercase, 64-bit integers get the `L` suffix, `decimal` invariant; `DateTime` throws — convert to epoch milliseconds; `ulong` above `long.MaxValue` throws (YQL is signed 64-bit) |

### Limit / Offset

`ToSearchRequest()` and `WithYql()` put each explicit builder value in the request. `.Limit(10).Offset(20)` sets `Hits=10` and `Offset=20`. An omitted value on `WithYql()` leaves that property at its existing value. You can set either property after applying the builder to choose a different page.

`Build()` produces standalone YQL. It renders the same window as `limit 30 offset 20`, because YQL's `limit` is the exclusive end of the slice. When you send raw YQL, inline pagination takes precedence over the request values. [Vespa YQL limit and offset](https://docs.vespa.ai/en/reference/querying/yql.html#limit-offset)

The query tensor name is derived automatically: for a field named `embedding`, the query input is `query(query_embedding)`.
