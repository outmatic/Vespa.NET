using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Vespa.Models.Attributes;
using Vespa.Query;

namespace Vespa.Benchmarks;

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
public class YqlBuilderBenchmarks
{
    // ── Model ─────────────────────────────────────────────────────────────────

    [VespaDocument("product", Namespace = "shop")]
    private sealed record Product
    {
        [VespaField(Name = "product_name")] public string Name { get; init; } = "";
        [VespaField(Name = "price")] public decimal Price { get; init; }
        public string Category { get; init; } = "";
    }

    // ── Simple queries ────────────────────────────────────────────────────────

    [BenchmarkCategory("Simple"), Benchmark(Baseline = true)]
    public string Untyped_SelectStar_From_Where()
        => YqlBuilder
            .Select()
            .From("product")
            .Where(w => w.Field("price").LessThan(500))
            .Build();

    [BenchmarkCategory("Simple"), Benchmark]
    public string Typed_SelectStar_Where()
        => YqlBuilder<Product>
            .Select()
            .Where(w => w.Field(p => p.Price).LessThan(500))
            .Build();

    [BenchmarkCategory("Simple"), Benchmark]
    public string Typed_SelectFields_Where()
        => YqlBuilder<Product>
            .Select(p => p.Name, p => p.Price)
            .Where(w => w.Field(p => p.Price).LessThan(500))
            .Build();

    // ── Compound queries ──────────────────────────────────────────────────────

    [BenchmarkCategory("Compound"), Benchmark(Baseline = true)]
    public string Compound_And_Or()
        => YqlBuilder
            .Select()
            .From("product")
            .Where(w => w
                .Field("price").GreaterThan(10)
                .And(a => a
                    .Field("category").Contains("electronics")
                    .Or(o => o.Field("category").Contains("computers"))))
            .OrderBy("price")
            .Limit(20)
            .Build();

    [BenchmarkCategory("Compound"), Benchmark]
    public string Compound_Typed_And_Or()
        => YqlBuilder<Product>
            .Select()
            .Where(w => w
                .Field(p => p.Price).GreaterThan(10)
                .And(a => a
                    .Field("category").Contains("electronics")
                    .Or(o => o.Field("category").Contains("computers"))))
            .OrderBy("price")
            .Limit(20)
            .Build();

    // ── Grouping ──────────────────────────────────────────────────────────────

    [BenchmarkCategory("Grouping"), Benchmark]
    public string Grouping_Build()
        => GroupingBuilder
            .All()
            .Group("category")
            .Max(10)
            .OrderByDescending(GroupingAgg.Count())
            .Each(e => e.Output(GroupingAgg.Count(), GroupingAgg.Avg("price")))
            .Build();

    [BenchmarkCategory("Grouping"), Benchmark]
    public string Grouping_FullQuery()
        => YqlBuilder<Product>
            .Select()
            .GroupBy(
                GroupingBuilder.All()
                    .Group("category")
                    .Max(10)
                    .Each(e => e.Output(GroupingAgg.Count())))
            .Build();
}
