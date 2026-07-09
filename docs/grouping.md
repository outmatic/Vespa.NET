# Grouping & Aggregation

Vespa.NET provides a fluent builder for Vespa's grouping expressions. Results are returned as typed `GroupingSearchResponse<T>`.

---

## Basic Grouping

```csharp
using Vespa.Query;

var request = new VespaSearchRequest
{
    Yql = YqlBuilder<Product>
        .Select()
        .GroupBy(
            GroupingBuilder.All()
                .Group("category")
                .Max(10)
                .OrderByDescending(GroupingAgg.Count())
                .Each(e => e.Output(GroupingAgg.Count(), GroupingAgg.Avg("price"))))
        .Build(),
    Hits = 0
};

var result = await client.Search.GroupByAsync<Product>(request);

foreach (var groupList in result.GroupingResults)
foreach (var group in groupList.Groups)
    Console.WriteLine($"{group.Value}: count={group.Aggregations["count()"]}");
```

`VespaGroup.Aggregations` holds the numeric outputs; non-numeric outputs (e.g.
`tostring(...)`/`toraw(...)`) are available as `JsonElement`s in
`VespaGroup.RawAggregations`.

---

## Grouping Extensions

### Fixed-width numeric buckets

```csharp
GroupingBuilder.All().GroupByFixedWidth("price", 100);
```

### Predefined buckets

Tuple buckets are inclusive of `From`, exclusive of `To`; values outside every
bucket land in the rest group. Use `GroupingBucket` for `inf` endpoints or
open/closed bounds:

```csharp
GroupingBuilder.All().GroupByBuckets("price", (0, 100), (100, 500));
// group(predefined(price, bucket[0,100), bucket[100,500)))

GroupingBuilder.All().GroupByBuckets("price",
    GroupingBucket.Numeric(null, 100),                      // bucket[-inf,100)
    GroupingBucket.Numeric(100, 500, toInclusive: true),    // bucket[100,500]
    GroupingBucket.Numeric(500, null));                     // bucket[500,inf)
```

### Filtering groups

`filter(...)` / `keep(...)` restrict which values are collected when grouping on
multivalue attributes (`where(true)` was removed from the grouping grammar):

```csharp
GroupingBuilder.All()
    .Group("my_array")
    .Filter(GroupingFilter.Regex("foo.*", "my_array"))
    .Each(e => e.Output(GroupingAgg.Count()));
// all(group(my_array) filter(regex("foo.*", my_array)) each(output(count())))

// Composable: range, istrue, not/and/or
GroupingFilter.Range(1990, 2012, "year", minInclusive: true, maxInclusive: true);
GroupingFilter.IsTrue("my_bool").And(GroupingFilter.Not(GroupingFilter.Regex(".*foo", "f")));
```

### Labels, parallel each blocks, max(inf), multi-key order

```csharp
GroupingBuilder.All()
    .Group("genre")
    .MaxAll()                                       // max(inf) — all groups
    .OrderByDescending(GroupingAgg.Count())
    .OrderByAscending(GroupingAgg.Avg("price"))     // order(-count(), +avg(price))
    .Each(e => e.Output(GroupingAgg.As(GroupingAgg.Count(), "n")).As("counts"))
    .Each(e => e.Output(GroupingAgg.Avg("year")).As("avgs"));   // parallel each blocks
```

### Per-group document summaries

Return the actual documents inside each group (`hitlist:*` in the response,
surfaced in `VespaGroup.Hits`; use `group.GetHits<T>()` for typed
`SearchHit<T>` access):

```csharp
GroupingBuilder.All().Group("category")
    .Each(e => e.Output(GroupingAgg.Count()).Summary(maxHits: 3));
// all(group(category) each(output(count()) max(3) each(output(summary()))))

var response = await client.Search.GroupByAsync<Product>(request);
foreach (var group in response.GroupingResults[0].Groups)
    foreach (var hit in group.GetHits<Product>())
        Console.WriteLine($"{group.Value}: {hit.Id} ({hit.Relevance})");
```

### Nested grouping

`SubGroup` may be called multiple times for parallel sub-groupings (label them
with `.As("name")`):

```csharp
GroupingBuilder.All().Group("category").Each(e => e
    .Output(GroupingAgg.Count())
    .SubGroup(GroupingBuilder.All().Group("brand").Each(inner => inner
        .Output(GroupingAgg.Count(), GroupingAgg.Avg("price")))));
```

### Paginated streaming

`GroupByStreamAsync` follows `GroupingSearchResponse.ContinuationTokens`
automatically, passing them back through the YQL `continuations` annotation
(Vespa has no `grouping.continuation` query parameter). For manual paging use
`GroupingContinuations.Apply(yql, tokens)`.

```csharp
await foreach (var page in client.Search.GroupByStreamAsync<Product>(request))
    foreach (var list in page.GroupingResults)
        Console.WriteLine(list.Label);
```

---

## Aggregation Reference

### Core Aggregations

| Function | Description |
|---|---|
| `Count()` | Number of documents in group |
| `Sum(field)` | Sum of field values |
| `Avg(field)` | Average of field values |
| `Min(field)` | Minimum value |
| `Max(field)` | Maximum value |
| `StdDev(field)` | Standard deviation |
| `Xor(field)` | XOR of field values |
| `Quantiles([0.5, 0.9], field)` | Quantile estimates (values 0–1) |
| `Relevance()` | Document relevance score |
| `Summary()` | Hits per group: `each(output(summary()))` |
| `Summary("class", maxHits: 3)` | Hits per group with summary class and limit |

Label any aggregator with `GroupingAgg.As(expr, "label")` → `expr as(label)`.

### Time Functions

| Function | Description |
|---|---|
| `TimeDate(field)` | Full date |
| `TimeYear(field)` | Year component |
| `TimeMonthOfYear(field)` | Month (1-12) |
| `TimeDayOfMonth(field)` | Day (1-31) |
| `TimeDayOfWeek(field)` | Day of week (0-6, Monday = 0) |
| `TimeDayOfYear(field)` | Day of year (0-365) |
| `TimeHourOfDay(field)` | Hour (0-23) |
| `TimeMinuteOfHour(field)` | Minute (0-59) |
| `TimeSecondOfMinute(field)` | Second (0-59) |

### Math Functions

| Function | Description |
|---|---|
| `MathLog(expr)` | Natural logarithm |
| `MathSqrt(expr)` | Square root |
| `MathPow(expr, n)` | Power |

(The grouping grammar has no `math.floor`, `math.abs` or `cmp`.)

### String / Conversion Functions

| Function | Description |
|---|---|
| `StrLen(expr)` | Byte length of a string |
| `StrCat(e1, e2, …)` | Concatenate expressions |
| `ToStringValue(expr)` / `ToLong(expr)` / `ToDouble(expr)` / `ToRaw(expr)` | Type conversion |

### List / Accessor Functions

| Function | Description |
|---|---|
| `Size(expr)` | Element count |
| `Sort(expr)` / `Reverse(expr)` | Sort / reverse a multivalue expression |
| `At(attribute, index)` | `attribute.at(index)` array access |
| `InterpolatedLookup(attribute, expr)` | Interpolated index lookup in a sorted array |
| `GeoDistance(attribute, lat, lon, miles?)` | `geo_distance(attribute(f), lat, lon).km` |

### Composite Functions

| Function | Description |
|---|---|
| `Cat(f1, f2)` | Concatenate |
| `Md5(field, maxLength, bits)` | MD5 hash |
| `Uca(field, "locale", "strength"?)` | Unicode collation |
| `XorBit(expr, width)` | XOR over `width` bits |
| `ZCurveX(field)` | Z-curve X coordinate |
| `ZCurveY(field)` | Z-curve Y coordinate |
| `DocIdNsSpecific()` | Namespace-specific document ID |

### Arithmetic

| Function | Description |
|---|---|
| `Add(a, b)` | Addition |
| `Sub(a, b)` | Subtraction |
| `Mul(a, b)` | Multiplication |
| `Div(a, b)` | Division |
| `Mod(a, b)` | Modulo |
| `Neg(expr)` | Negation |
| `FixedWidth(field, width)` | Fixed-width bucketing |
