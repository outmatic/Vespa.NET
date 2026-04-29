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
// select * from product where price < 500 limit 20;

// Specific fields — names resolved via [VespaField]
var yql2 = YqlBuilder<Product>
    .Select(p => p.Name, p => p.Price)
    .Where(w => w.Field(p => p.Price).GreaterThan(10)
                 .And(sub => sub.Field(p => p.Name).Contains("laptop")))
    .OrderBy(p => p.Price, descending: true)
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
    .OrderBy("price", descending: true)
    .Build();
```

---

## Field Predicates

| Method | YQL Output |
|---|---|
| `.Contains("text")` | `field contains "text"` |
| `.ContainsAnnotated("text", filter: true)` | `field contains ({filter: true}"text")` |
| `.Text("value")` | `field contains text("value")` |
| `.Matches("regex.*")` | `field matches "regex.*"` |
| `.EqualTo(val)` | `field = val` |
| `.GreaterThan(n)` / `.LessThan(n)` | `field > n` / `field < n` |
| `.GreaterOrEqual(n)` / `.LessOrEqual(n)` | `field >= n` / `field <= n` |
| `.Range(from, to)` | `range(field, from, to)` |
| `.In("a", "b")` | `field in ("a", "b")` |
| `.Phrase("w1", "w2")` | `field contains phrase("w1", "w2")` |
| `.Fuzzy("term", maxEditDistance: 2)` | `field contains ({maxEditDistance:2}fuzzy("term"))` |
| `.Near(["t1", "t2"], distance: 3)` | `field contains ({distance: 3}near("t1", "t2"))` |
| `.ONear(["t1", "t2"])` | `field contains onear("t1", "t2")` |
| `.Equiv("t1", "t2")` | `field contains equiv("t1", "t2")` |
| `.SameElement(...)` | `field contains sameElement(...)` |
| `.Uri("vespa.ai")` | `field contains uri("vespa.ai")` |

## Clause-Level Predicates

| Method | YQL Output |
|---|---|
| `w.NearestNeighbor("emb", "q", 10)` | `{targetHits:10}nearestNeighbor(emb, q)` |
| `w.WeakAnd(s => ...)` | `weakAnd(...)` |
| `w.Wand("f", terms, targetHits: 100)` | `{targetHits:100}wand(f, {...})` |
| `w.DotProduct("f", terms)` | `dotProduct(f, {...})` |
| `w.WeightedSet("f", tokens)` | `weightedSet(f, {...})` |
| `w.GeoLocation("pos", lat, lon, 10.0)` | `geoLocation(pos, lat, lon, "10km")` |
| `w.GeoBoundingBox("pos", s, w, n, e)` | `{southWest:..., northEast:...}geoBoundingBox(pos)` |
| `w.UserQuery("free text")` | `userQuery("free text")` |
| `w.UserInput("param")` | `userInput(@param)` |
| `w.Rank(...)` | `rank(...)` |
| `w.NonEmpty(...)` | `nonEmpty(...)` |
| `w.Or(or => { or.A(); or.B(); })` | `A or B` |
| `w.AnyOf(p => p.A(), p => p.B())` | `A or B` |
| `w.HybridSearch("emb", "q", "param", 100)` | `rank({targetHits:100}nearestNeighbor(emb, q), userInput(@param))` |
| `w.Predicate("f", attrs, ranges)` | `predicate(f, {...}, {...})` |

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
.Where(w => w.Field(p => p.Status).EqualTo("active")
    .Or(or =>
    {
        or.Field(p => p.Pinned).EqualTo(1);
        or.Field(p => p.ExpiresAt).EqualTo(0);
        or.Field(p => p.ExpiresAt).GreaterThan(now);
    }))
// status = "active" or pinned = 1 or expires_at = 0 or expires_at > 1000
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

`ToSearchRequest()` captures YQL, Hits, and Offset. Chain helpers for ranking and tensors:

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

The query tensor name is derived automatically: for a field named `embedding`, the query input is `query(query_embedding)`.
