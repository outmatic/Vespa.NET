# Grouping & Aggregation

Vespa.NET provides a fluent builder for Vespa's grouping expressions. Results are returned as
`GroupingSearchResponse<T>`; call `.AsTyped()` for a grouping hierarchy with typed document summaries.

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
    Console.WriteLine($"{group.Value}: count={group.GetAggregation<long>("count()")}");
```

`VespaGroup.Aggregations` holds the numeric outputs; non-numeric outputs (e.g.
`tostring(...)`/`toraw(...)`) are available as `JsonElement`s in
`VespaGroup.RawAggregations`.

### Typed results and exact aggregation values

Call `.AsTyped()` once to obtain a `TypedGroupingSearchResponse<T>`. Its
`GroupingResults` and `GroupingRoots` contain `VespaGroupList<T>` and
`VespaGroup<T>` at every level, so `group.Hits` is an
`IReadOnlyList<SearchHit<T>>` and `group.SubGroups` retains the same document type:

```csharp
var result = (await client.Search.GroupByAsync<Product>(request)).AsTyped();

foreach (var list in result.GroupingResults)
foreach (var group in list.Groups)
{
    long count = group.GetAggregation<long>("count()");
    double average = group.GetAggregation<double>("avg(price)");

    foreach (var hit in group.Hits) // populated when the query requests summaries
        Console.WriteLine($"{group.Value}: {hit.Fields?.Name}");
}
```

The projection runs in memory, makes no HTTP requests and leaves the original
response available. It preserves ordinary hits, root outputs and summaries,
rest buckets, nested groups, coverage, errors, timing, trace and continuation
tokens. Existing `GroupByAsync<T>`, `GetHits<T>()` and response constructors remain
available. Manually constructed responses must contain `SearchHit<T>` summaries
matching their document type; mismatched summaries throw `InvalidCastException`.

Both typed and original groups expose `GetAggregation<TValue>(name)` and
`TryGetAggregation<TValue>(name, out value)`. They deserialize the original
`RawAggregations` JSON instead of converting the legacy `double` values.
This preserves 64-bit integer values beyond `2^53` and the precision present
in JSON decimal values. Choosing `double` still uses floating-point precision;
choosing `decimal` cannot recover precision already lost by the server.

Vespa returns `count()` as a long, and outputs can also contain structured
values such as the array returned by `quantiles(...)` (see the official
[grouping reference](https://docs.vespa.ai/en/reference/querying/grouping-language.html#group-aggregators)).
Select the corresponding CLR type, or use `JsonElement` for arbitrary JSON:

```csharp
using System.Text.Json;

long total = group.GetAggregation<long>("count()");
decimal amount = group.GetAggregation<decimal>("sum(price)");
JsonElement quantiles = group.GetAggregation<JsonElement>("quantiles([0.5, 0.9], price)");

if (group.TryGetAggregation<double>("avg(price)", out var average))
    Console.WriteLine(average);
```

Use an aggregation's alias, such as `"total"`, when the query uses
`count() as(total)`. Structured outputs can also be read into arrays or DTOs;
an optional `JsonSerializerOptions` argument controls deserialization. By
default, standard `System.Text.Json` rules apply, including case-sensitive
property matching and no conversion of JSON strings to numbers.
For string-rendered `"NaN"` or infinities, pass options with
`NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals`.

| Situation | `GetAggregation<TValue>` | `TryGetAggregation<TValue>` |
|---|---|---|
| Name absent from `RawAggregations` | `KeyNotFoundException` | `false`, default output |
| JSON incompatible with the requested type or integer/decimal overflow | `JsonException` | `false`, default output |
| JSON null, nullable/reference target | `null` | `true`, null output |
| JSON null, non-nullable value target | `JsonException` | `false`, default output |
| Unsupported CLR type or invalid serializer configuration | Exception | Exception |

Null or empty names throw an argument exception in both methods. Manually
constructed groups must populate `RawAggregations` to use these accessors;
there is no fallback to potentially rounded `Aggregations` values.

`GroupingSearchResponse.Coverage`, `Errors`, and `Trace` retain the same metadata
as a regular search response. Check coverage and errors when consuming results:
Vespa can return partial results with HTTP 200.

`GroupingRoots` contains the root of each grouping expression, including its
aggregations and hit lists. For example, a request containing
`| all(output(count()))` exposes the count through
`result.GroupingRoots[0].GetAggregation<long>("count()")`.

For numeric aggregations over all matched documents, create a single constant
group, for example `all(group(1) each(output(sum(price))))`, and read the value
from `result.GroupingResults[0].Groups[0]`. This follows the official
[whole-document aggregation example](https://docs.vespa.ai/en/querying/grouping.html#aggregating-over-all-documents).

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

Rest groups are retained in the results: `group.IsRestBucket` identifies them,
and `group.Id` preserves Vespa's `group:null` identity. Their aggregation values
are included just like those of the explicit buckets.

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

Return the actual documents inside each group (`hitlist:*` in the response).
Use `.AsTyped()` to read `SearchHit<T>` directly from `group.Hits`:

```csharp
var request = YqlBuilder<Product>.Select()
    .GroupBy(GroupingBuilder.All().Group("category")
        .Each(e => e.Output(GroupingAgg.Count()).Summary(maxHits: 3)))
    .ToSearchRequest();
// all(group(category) each(output(count()) max(3) each(output(summary()))))

var response = (await client.Search.GroupByAsync<Product>(request)).AsTyped();
foreach (var group in response.GroupingResults[0].Groups)
    foreach (var hit in group.Hits)
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
    foreach (var list in page.AsTyped().GroupingResults)
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
