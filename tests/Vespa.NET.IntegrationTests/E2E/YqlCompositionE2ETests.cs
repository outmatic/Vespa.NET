using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Models.Tensors;
using Vespa.Query;
using Vespa.Search;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

/// <summary>
/// E2E tests validating that YQL boolean composition generates valid queries
/// accepted by Vespa's YQL parser. These tests catch issues that unit tests miss
/// (e.g. parenthesization, operator precedence inside rank/weakAnd).
/// </summary>
[Collection("Vespa")]
[Trait("Category", "E2E")]
public class YqlCompositionE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    private static string NewId() => $"yql-comp-{Guid.NewGuid():N}";

    private static TestProduct MakeProduct(string name, double price, string category, int quantity = 10) => new()
    {
        Name = name,
        Price = price,
        Category = category,
        Quantity = quantity,
        InStock = true,
        Tag = "yql-e2e",
        Embedding = VespaTensor.FromDenseValues([
            (float)(price / 100.0), category == "electronics" ? 1f : 0f,
            category == "books" ? 1f : 0f, (float)(quantity / 50.0)
        ])
    };

    private async Task<List<string>> SeedAsync()
    {
        var products = new[]
        {
            MakeProduct("Laptop", 999.99, "electronics", 5),
            MakeProduct("Phone", 499.99, "electronics", 20),
            MakeProduct("Novel", 19.99, "books", 100),
            MakeProduct("Textbook", 79.99, "books", 30),
            MakeProduct("Headphones", 149.99, "electronics", 50),
        };

        var ids = new List<string>();
        foreach (var p in products)
        {
            var id = NewId();
            ids.Add(id);
            await fixture.Client.Documents.PutAsync<TestProduct>(id, p);
        }
        await Task.Delay(2000);
        return ids;
    }

    private async Task CleanupAsync(List<string> ids)
    {
        foreach (var id in ids)
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    // --- And composition ---

    [Fact]
    public async Task And_MultiplePredicates_ValidYql()
    {
        if (!Enabled) return;
        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w =>
                {
                    w.Field(p => p.Category).Contains("electronics");
                    w.Field(p => p.Price).LessThan(600);
                })
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.True(result.Root.Children.Count > 0);
        }
        finally { await CleanupAsync(ids); }
    }

    // --- Or — single predicate ---

    [Fact]
    public async Task Or_SinglePredicate_ValidYql()
    {
        if (!Enabled) return;
        var ids = await SeedAsync();
        try
        {
            // After Field().Contains() we're on YqlWhereClause (untyped), so Or takes Action<YqlWhereClause>
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w
                    .Field(p => p.Category).Contains("electronics")
                    .Or(or => or.Field("category").Contains("books")))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.True(result.Root.Children.Count >= 2);
        }
        finally { await CleanupAsync(ids); }
    }

    // --- Or — multiple predicates in callback (new semantics: OR'd, not AND'd) ---

    [Fact]
    public async Task Or_MultiplePredicatesInCallback_ValidYql()
    {
        if (!Enabled) return;
        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w
                    .Field(p => p.Price).GreaterThan(1000)
                    .Or(or =>
                    {
                        or.Field("category").Contains("books");
                        or.Field("quantity").GreaterThan(40);
                    }))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            // price > 1000 (none) OR category=books OR quantity>40 → should match
            Assert.True(result.Root.Children.Count > 0);
        }
        finally { await CleanupAsync(ids); }
    }

    // --- Or on empty clause ---

    [Fact]
    public async Task Or_OnEmptyClause_ValidYql()
    {
        if (!Enabled) return;
        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w.Or(or =>
                {
                    or.Field("category").Contains("electronics");
                    or.Field("category").Contains("books");
                }))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.True(result.Root.Children.Count >= 2);
        }
        finally { await CleanupAsync(ids); }
    }

    // --- AnyOf ---

    [Fact]
    public async Task AnyOf_FlatOr_ValidYql()
    {
        if (!Enabled) return;
        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w.AnyOf(
                    p => p.Field("category").Contains("electronics"),
                    p => p.Field("category").Contains("books")))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.True(result.Root.Children.Count >= 2);
        }
        finally { await CleanupAsync(ids); }
    }

    // --- And + nested Or ---

    [Fact]
    public async Task And_With_NestedOr_ValidYql()
    {
        if (!Enabled) return;
        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w =>
                {
                    w.Field(p => p.InStock).EqualTo(true);
                    w.And(a => a.Or(or =>
                    {
                        or.Field("category").Contains("electronics");
                        or.Field("category").Contains("books");
                    }));
                })
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.True(result.Root.Children.Count >= 2);
        }
        finally { await CleanupAsync(ids); }
    }

    // --- And + AnyOf ---

    [Fact]
    public async Task And_With_AnyOf_ValidYql()
    {
        if (!Enabled) return;
        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w =>
                {
                    w.Field(p => p.Price).LessThan(1000);
                    w.And(a => a.AnyOf(
                        p => p.Field("category").Contains("electronics"),
                        p => p.Field("quantity").GreaterThan(90)));
                })
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.True(result.Root.Children.Count > 0);
        }
        finally { await CleanupAsync(ids); }
    }

    // --- Rank ---

    [Fact]
    public async Task Rank_TwoOperands_ValidYql()
    {
        if (!Enabled) return;
        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w.Rank(
                    match => match.Field("category").Contains("electronics"),
                    rank1 => rank1.Field("product_name").Contains("laptop")))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.True(result.Root.Children.Count > 0);
        }
        finally { await CleanupAsync(ids); }
    }

    // --- HybridSearch ---

    [Fact]
    public async Task HybridSearch_ValidYql()
    {
        if (!Enabled) return;
        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w.HybridSearch("embedding", "query_embedding", "userQuery", targetHits: 5))
                .Build();

            var request = new VespaSearchRequest
            {
                Yql = yql,
                Hits = 5,
                Ranking = new RankingConfig { Profile = "closeness_profile" },
                Input = new() { ["query(query_embedding)"] = VespaTensor.FromDenseValues([5f, 1f, 0f, 0.1f]) },
                CustomParameters = new() { ["userQuery"] = "laptop" }
            };

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            Assert.True(result.Root.Children.Count > 0);
        }
        finally { await CleanupAsync(ids); }
    }

    // --- HybridSearch + filters ---

    [Fact]
    public async Task HybridSearch_WithFilters_ValidYql()
    {
        if (!Enabled) return;
        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w =>
                {
                    w.Field(p => p.Category).Contains("electronics");
                    w.HybridSearch("embedding", "query_embedding", "userQuery", targetHits: 5);
                })
                .Build();

            var request = new VespaSearchRequest
            {
                Yql = yql,
                Hits = 5,
                Ranking = new RankingConfig { Profile = "closeness_profile" },
                Input = new() { ["query(query_embedding)"] = VespaTensor.FromDenseValues([5f, 1f, 0f, 0.1f]) },
                CustomParameters = new() { ["userQuery"] = "laptop" }
            };

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            foreach (var hit in result.Root.Children)
                Assert.Equal("electronics", hit.Fields?.Category);
        }
        finally { await CleanupAsync(ids); }
    }

    // --- Chained Or ---

    [Fact]
    public async Task Chained_Or_ValidYql()
    {
        if (!Enabled) return;
        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w
                    .Field(p => p.Price).GreaterThan(900)
                    .Or(or => or.Field("price").LessThan(25))
                    .Or(or => or.Field("quantity").GreaterThan(40)))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.True(result.Root.Children.Count >= 2);
        }
        finally { await CleanupAsync(ids); }
    }

    // --- WeakAnd ---

    [Fact]
    public async Task WeakAnd_ValidYql()
    {
        if (!Enabled) return;
        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w.WeakAnd(wa =>
                {
                    wa.Field("category").Contains("electronics");
                    wa.Field("product_name").Contains("laptop");
                }))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.True(result.Root.Children.Count > 0);
        }
        finally { await CleanupAsync(ids); }
    }

    // --- NonEmpty ---

    [Fact]
    public async Task NonEmpty_ValidYql()
    {
        if (!Enabled) return;
        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w.NonEmpty(ne => ne.Field("category").Contains("electronics")))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.True(result.Root.Children.Count > 0);
        }
        finally { await CleanupAsync(ids); }
    }
}
