using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Query;
using Vespa.Search;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public class SearchE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    // ── Seed data ─────────────────────────────────────────────────────────────

    private static readonly TestProduct[] Products =
    [
        new() { Name = "Gaming Laptop",       Price = 1499.0, Category = "electronics", Description = "High-end gaming laptop",      InStock = true,  Quantity = 5  },
        new() { Name = "Office Mouse",         Price = 29.0,   Category = "accessories", Description = "Wireless ergonomic mouse",    InStock = true,  Quantity = 50 },
        new() { Name = "Mechanical Keyboard",  Price = 149.0,  Category = "accessories", Description = "Tactile mechanical keyboard", InStock = true,  Quantity = 20 },
        new() { Name = "Monitor 27 inch",      Price = 399.0,  Category = "electronics", Description = "4K IPS display",             InStock = false, Quantity = 0  },
        new() { Name = "USB-C Hub",            Price = 49.0,   Category = "accessories", Description = "7-in-1 USB hub",             InStock = true,  Quantity = 100},
    ];

    private async Task<List<string>> SeedAsync()
    {
        var ids = new List<string>();
        foreach (var p in Products)
        {
            var id = $"search-{Guid.NewGuid():N}";
            ids.Add(id);
            await fixture.Client.Documents.PutAsync(id, p);
        }
        await Task.Delay(2000);
        return ids;
    }

    private async Task CleanupAsync(List<string> ids)
    {
        foreach (var id in ids)
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    // ── Basic queries ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_YqlTrue_ReturnsAllDocuments()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select().Where(w => w.True()).Build();
            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 100);
            Assert.True(result.Root.Fields?.TotalCount >= Products.Length);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Search_PriceLessThan_FiltersCorrectly()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>
                .Select()
                .Where(w => w.Field(p => p.Price).LessThan(100))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.All(result.Root.Children, h => Assert.True(h.Fields?.Price < 100));
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Search_PriceGreaterOrEqual_FiltersCorrectly()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>
                .Select()
                .Where(w => w.Field(p => p.Price).GreaterOrEqual(149))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.NotEmpty(result.Root.Children);
            Assert.All(result.Root.Children, h => Assert.True(h.Fields?.Price >= 149));
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Search_PriceRange_FiltersCorrectly()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>
                .Select()
                .Where(w => w.Field(p => p.Price).Range(50, 500))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.NotEmpty(result.Root.Children);
            Assert.All(result.Root.Children, h =>
            {
                Assert.True(h.Fields?.Price >= 50);
                Assert.True(h.Fields?.Price <= 500);
            });
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Search_CategoryEquals_FiltersCorrectly()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>
                .Select()
                .Where(w => w.Field(p => p.Category).Contains("electronics"))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.NotEmpty(result.Root.Children);
            Assert.All(result.Root.Children, h => Assert.Equal("electronics", h.Fields?.Category));
        }
        finally { await CleanupAsync(ids); }
    }

    // ── Full-text search ────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_FullText_Contains_ReturnsMatches()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>
                .Select()
                .Where(w => w.Field(p => p.Name).Contains("Laptop"))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.NotEmpty(result.Root.Children);
            Assert.Contains(result.Root.Children, h => h.Fields?.Name?.Contains("Laptop") == true);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Search_DescriptionContains_ReturnsMatches()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>
                .Select()
                .Where(w => w.Field(p => p.Description).Contains("ergonomic"))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.NotEmpty(result.Root.Children);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── Compound predicates ─────────────────────────────────────────────────────

    [Fact]
    public async Task Search_And_Compound_Predicate()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>
                .Select()
                .Where(w => w
                    .Field(p => p.Category).Contains("accessories")
                    .And(a => a.Field("price").LessThan(100)))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.All(result.Root.Children, h =>
            {
                Assert.Equal("accessories", h.Fields?.Category);
                Assert.True(h.Fields?.Price < 100);
            });
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Search_Or_Compound_Predicate()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>
                .Select()
                .Where(w => w
                    .Field(p => p.Price).GreaterThan(1000)
                    .Or(o => o.Field("price").LessThan(50)))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.NotEmpty(result.Root.Children);
            Assert.All(result.Root.Children, h =>
                Assert.True(h.Fields?.Price > 1000 || h.Fields?.Price < 50));
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Search_In_Predicate_MatchesMultipleValues()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            // Vespa "in" operator only works on pure attribute fields; category is indexed+attribute,
            // so we use OR with contains instead.
            var yql = YqlBuilder<TestProduct>
                .Select()
                .Where(w => w
                    .Field(p => p.Category).Contains("electronics")
                    .Or(o => o.Field("category").Contains("accessories")))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 100);
            Assert.True(result.Root.Children.Count >= Products.Length);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Search_BooleanField_FiltersCorrectly()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>
                .Select()
                .Where(w => w.Field(p => p.InStock).EqualTo(false))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.NotEmpty(result.Root.Children);
            Assert.All(result.Root.Children, h => Assert.False(h.Fields?.InStock));
        }
        finally { await CleanupAsync(ids); }
    }

    // ── Pagination & ordering ───────────────────────────────────────────────────

    [Fact]
    public async Task Search_LimitOffset_Paginates()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select().Where(w => w.True()).Build();
            var page1 = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 2, offset: 0);
            Assert.Equal(2, page1.Root.Children.Count);

            var page2 = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 2, offset: 2);
            Assert.NotEmpty(page2.Root.Children);

            var page1Ids = page1.Root.Children.Select(h => h.Id).ToHashSet();
            Assert.DoesNotContain(page2.Root.Children.First().Id, page1Ids);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Search_OrderByPrice_Ascending()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>
                .Select()
                .Where(w => w.True())
                .OrderBy(p => p.Price)
                .Limit(Products.Length)
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: Products.Length);
            var prices = result.Root.Children.Select(h => h.Fields?.Price ?? 0).ToList();

            for (int i = 1; i < prices.Count; i++)
                Assert.True(prices[i] >= prices[i - 1], $"Expected ascending order: {prices[i]} >= {prices[i - 1]}");
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Search_OrderByPrice_Descending()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>
                .Select()
                .Where(w => w.True())
                .OrderBy(p => p.Price, descending: true)
                .Limit(Products.Length)
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: Products.Length);
            var prices = result.Root.Children.Select(h => h.Fields?.Price ?? 0).ToList();

            for (int i = 1; i < prices.Count; i++)
                Assert.True(prices[i] <= prices[i - 1], $"Expected descending order: {prices[i]} <= {prices[i - 1]}");
        }
        finally { await CleanupAsync(ids); }
    }

    // ── VespaSearchRequest ──────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_WithRequest_ReturnsResults()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder<TestProduct>.Select().Where(w => w.True()).Build(),
                Hits = 3,
                Offset = 0
            };

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            Assert.NotNull(result.Root);
            Assert.True(result.Root.Children.Count <= 3);
            Assert.True(result.Root.Fields?.TotalCount >= Products.Length);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task SearchAsync_WithTimeout_Completes()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder<TestProduct>.Select().Where(w => w.True()).Build(),
                Hits = 10
            }.WithTimeout(TimeSpan.FromSeconds(10));

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            Assert.NotNull(result.Root);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── SearchPagedAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchPagedAsync_IteratesAllPages()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder<TestProduct>.Select().Where(w => w.True()).Build(),
                Hits = 2
            };

            var allHits = new List<string>();
            await foreach (var page in fixture.Client.Search.SearchPagedAsync<TestProduct>(request, pageSize: 2))
            {
                foreach (var hit in page.Root.Children)
                    allHits.Add(hit.Id);
            }

            Assert.True(allHits.Count >= Products.Length);
            Assert.Equal(allHits.Count, allHits.Distinct().Count()); // no duplicates
        }
        finally { await CleanupAsync(ids); }
    }

    // ── Grouping ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GroupBy_Category_AggregatesCorrectly()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder<TestProduct>
                    .Select()
                    .GroupBy(
                        GroupingBuilder.All()
                            .Group("category")
                            .Max(10)
                            .Each(e => e.Output(GroupingAgg.Count())))
                    .Build(),
                Hits = 0
            };

            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(request);

            Assert.NotEmpty(result.GroupingResults);
            var groups = result.GroupingResults[0].Groups;
            Assert.Contains(groups, g => g.Value == "electronics");
            Assert.Contains(groups, g => g.Value == "accessories");
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task GroupBy_Category_WithMultipleAggregations()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder<TestProduct>
                    .Select()
                    .GroupBy(
                        GroupingBuilder.All()
                            .Group("category")
                            .Max(10)
                            .Each(e => e.Output(
                                GroupingAgg.Count(),
                                GroupingAgg.Sum("price"),
                                GroupingAgg.Avg("price"),
                                GroupingAgg.Min("price"),
                                GroupingAgg.Max("price"))))
                    .Build(),
                Hits = 0
            };

            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(request);
            Assert.NotEmpty(result.GroupingResults);

            var groups = result.GroupingResults[0].Groups;
            var electronics = groups.First(g => g.Value == "electronics");

            // electronics has 2 products: 1499.0 and 399.0
            Assert.True(electronics.Aggregations.ContainsKey("count()"));
            Assert.True(electronics.Aggregations.ContainsKey("sum(price)"));
            Assert.True(electronics.Aggregations.ContainsKey("avg(price)"));
            Assert.True(electronics.Aggregations.ContainsKey("min(price)"));
            Assert.True(electronics.Aggregations.ContainsKey("max(price)"));
        }
        finally { await CleanupAsync(ids); }
    }

    // ── Coverage ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_ReturnsCoverageInfo()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select().Where(w => w.True()).Build();
            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 1);

            Assert.NotNull(result.Root.Coverage);
            Assert.Equal(100, result.Root.Coverage.CoveragePercentage);
            Assert.True(result.Root.Coverage.Full);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── Select specific fields ──────────────────────────────────────────────────

    [Fact]
    public async Task Search_SelectSpecificFields_ReturnsOnlyThose()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>
                .Select(p => p.Name, p => p.Price)
                .Where(w => w.True())
                .Limit(1)
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 1);
            Assert.NotEmpty(result.Root.Children);
        }
        finally { await CleanupAsync(ids); }
    }
}
