# Ranking DSL

Vespa.NET provides a fluent `RankingBuilder` for constructing `RankingConfig` objects — profiles, features, match-phase, soft timeout, diversity, and more.

---

## Builder

```csharp
using Vespa.Query;

var ranking = RankingBuilder
    .WithProfile("semantic")
    .Feature("query(threshold)", 0.8)
    .Feature("query(embedding)", queryEmbedding)
    .RerankCount(200)
    .SoftTimeout(enable: true, factor: 0.7)
    .MatchPhase("popularity", maxHits: 10000)
    .MatchPhaseDiversity("category", minGroups: 5)
    .GlobalPhaseRerankCount(100)
    .Matching(numThreadsPerSearch: 4);

var request = new VespaSearchRequest { Yql = yql }.WithRanking(ranking);
```

## Sort specification

Use `SortingBuilder` to set `ranking.sorting` independently of the YQL. Put the primary key first and append tie-breakers in order. The model selectors resolve the attribute names from the same mappings as `YqlBuilder`.

```csharp
var sorting = new SortingBuilder()
    .OrderBy<Product>(p => p.Price)
    .OrderBy<Product>(p => p.ProductId, SortDirection.Descending);

var request = new VespaSearchRequest { Yql = yql }
    .WithRanking(RankingBuilder.WithProfile("unranked").Sorting(sorting.Build()));
```

Sort keys use ascending order by default. Use `OrderByRelevance()` to sort by rank score, highest first by default. Pass functions such as `uca(...)` as a native expression to `RankingBuilder.Sorting(string)`. Vespa applies the YQL `order by` when both forms are present. [Sort grammar](https://docs.vespa.ai/en/reference/querying/sorting-language.html) · [YQL order by](https://docs.vespa.ai/en/reference/querying/yql.html#order-by)

---

## Code-First Rank Profiles

Define rank profiles directly on your model with `[VespaRankProfile]`:

```csharp
[VespaDocument("product")]
[VespaRankProfile("semantic",
    Inherits = "default",
    FirstPhase = "closeness(field, embedding)",
    SecondPhase = "closeness(field, embedding) * attribute(popularity)",
    SecondPhaseRerankCount = 200)]
[VespaRankFunction("freshness", "now - attribute(timestamp)", Profile = "semantic")]
public record Product { ... }
```

Functions are declared with one `[VespaRankFunction(signature, expression)]` per
function. `Profile` may be omitted when the type declares a single rank profile.
(The legacy semicolon-separated `Functions` string property still works but is
obsolete — it cannot express expressions containing `:` or `;`.)

### Rank Profile Options

| Property | Description |
|---|---|
| `Name` | Profile name |
| `Inherits` | Parent profile to inherit from |
| `FirstPhase` | First-phase ranking expression |
| `SecondPhase` | Second-phase ranking expression |
| `SecondPhaseRerankCount` | Number of documents to re-rank in second phase |
| `GlobalPhase` | Global-phase ranking expression |
| `GlobalPhaseRerankCount` | Number of documents to re-rank in global phase |
| `SummaryFeatures` | Features included in search results |
| `DiversityAttribute` | Attribute field for result diversity |
| `DiversityMinGroups` | Minimum number of diverse groups |
| `Functions` | *(obsolete)* Custom ranking functions — use `[VespaRankFunction]` instead |

---

## RankingBuilder Methods

| Method | What It Configures |
|---|---|
| `.WithProfile(name)` | Ranking profile name |
| `.Feature(name, value)` | Query feature (tensor or scalar) |
| `.RerankCount(n)` | Second-phase rerank count per node (`ranking.secondPhase.rerankCount`) |
| `.TotalRerankCount(n)` | Second-phase rerank count over all content nodes (preferred) |
| `.SecondPhaseRankScoreDropLimit(x)` | Second-phase score drop limit |
| `.KeepRankCount(n)` / `.TotalKeepRankCount(n)` | Hits keeping their rank score after first phase |
| `.SoftTimeout(enable, factor)` | Soft timeout with coverage factor |
| `.MatchPhase(field, maxHits, direction, totalMaxHits)` | Match-phase early termination. Prefer `totalMaxHits` and omit `direction` to use Vespa's configured order. |
| `.MatchPhaseDiversity(field, minGroups)` | Diversity on match-phase |
| `.GlobalPhaseRerankCount(n)` | Global-phase rerank count |
| `.GlobalPhaseRankScoreDropLimit(x)` | Global-phase score drop limit |
| `.Matching(numThreadsPerSearch)` | Matching thread configuration |
