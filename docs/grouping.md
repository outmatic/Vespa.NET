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

---

## Grouping Extensions

### Fixed-width numeric buckets

```csharp
GroupingBuilder.All().GroupByFixedWidth("price", 100);
```

### Predefined buckets

```csharp
GroupingBuilder.All().GroupByBuckets("price", (0, 100), (100, 500), (500, double.MaxValue));
```

### Having filter

```csharp
GroupingBuilder.All().Group("category").Having("count() > 5");
```

### Nested grouping

```csharp
GroupingBuilder.All().Group("category").Each(e => e
    .Output(GroupingAgg.Count())
    .SubGroup(GroupingBuilder.All().Group("brand").Each(inner => inner
        .Output(GroupingAgg.Count(), GroupingAgg.Avg("price")))));
```

### Paginated streaming

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

### Time Functions

| Function | Description |
|---|---|
| `TimeDate(field)` | Full date |
| `TimeYear(field)` | Year component |
| `TimeMonthOfYear(field)` | Month (1-12) |
| `TimeDayOfMonth(field)` | Day (1-31) |
| `TimeHourOfDay(field)` | Hour (0-23) |
| `TimeMinuteOfHour(field)` | Minute (0-59) |
| `TimeSecondOfMinute(field)` | Second (0-59) |

### Math Functions

| Function | Description |
|---|---|
| `MathFloor(expr)` | Floor |
| `MathLog(expr)` | Natural logarithm |
| `MathSqrt(expr)` | Square root |
| `MathAbs(expr)` | Absolute value |
| `MathPow(expr, n)` | Power |

### Composite Functions

| Function | Description |
|---|---|
| `Cat(f1, f2)` | Concatenate |
| `Md5(field, maxLength, bits)` | MD5 hash |
| `Uca(field, "locale", "strength"?)` | Unicode collation |
| `ZCurveX(field)` | Z-curve X coordinate |
| `ZCurveY(field)` | Z-curve Y coordinate |
| `DocIdNsSpecific()` | Namespace-specific document ID |

### Arithmetic

| Function | Description |
|---|---|
| `Add(a, b)` | Addition |
| `Mul(a, b)` | Multiplication |
| `Div(a, b)` | Division |
| `Mod(a, b)` | Modulo |
| `Neg(expr)` | Negation |
| `Cmp(a, b)` | Comparison (-1, 0, 1) |
| `FixedWidth(field, width)` | Fixed-width bucketing |
