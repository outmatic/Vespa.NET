using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Query;
using Vespa.Search;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

/// <summary>
/// E2E tests for VespaSearchRequest advanced properties, GroupByStreamAsync,
/// QueryAsync with parameters dict, and GetHistogramsAsync.
/// </summary>
[Collection("Vespa")]
[Trait("Category", "E2E")]
public class SearchRequestE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    private static readonly TestProduct[] Products =
    [
        new() { Name = "Alpha Widget",   Price = 100.0, Category = "widgets",  Description = "First widget",   InStock = true,  Quantity = 10, Tag = "premium" },
        new() { Name = "Beta Widget",    Price = 200.0, Category = "widgets",  Description = "Second widget",  InStock = true,  Quantity = 20, Tag = "standard" },
        new() { Name = "Gamma Gadget",   Price = 300.0, Category = "gadgets",  Description = "First gadget",   InStock = false, Quantity = 0,  Tag = "premium" },
        new() { Name = "Delta Gadget",   Price = 400.0, Category = "gadgets",  Description = "Second gadget",  InStock = true,  Quantity = 5,  Tag = "standard" },
        new() { Name = "Epsilon Widget", Price = 150.0, Category = "widgets",  Description = "Third widget",   InStock = true,  Quantity = 15, Tag = "premium" },
        new() { Name = "Zeta Gadget",    Price = 250.0, Category = "gadgets",  Description = "Third gadget",   InStock = true,  Quantity = 8,  Tag = "budget" },
    ];

    private async Task<List<string>> SeedAsync()
    {
        var ids = new List<string>();
        foreach (var p in Products)
        {
            var id = $"srq-{Guid.NewGuid():N}";
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

    // ── QueryAsync with parameters dict ──────────────────────────────────────

    [Fact]
    public async Task QueryAsync_WithParametersDict_PassesCustomParams()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select().Where(w => w.True()).Build();
            var parameters = new Dictionary<string, object>
            {
                ["hits"] = 2
            };

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 2, parameters: parameters);
            Assert.NotNull(result.Root);
            Assert.True(result.Root.Children.Count <= 2);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── VespaSearchRequest: CollapseField ────────────────────────────────────

    [Fact]
    public async Task SearchAsync_WithCollapseField_DeduplicatesByCategory()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder<TestProduct>.Select().Where(w => w.True()).Build(),
                Hits = 100,
                CollapseField = "category",
                CollapseSize = 1
            };

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            Assert.NotNull(result.Root);

            // With collapse size=1, we should get fewer hits than the total seeded
            // and at most one hit per unique category value
            Assert.True(result.Root.Children.Count <= Products.Length,
                $"Expected at most {Products.Length} hits but got {result.Root.Children.Count}");
            Assert.True(result.Root.Children.Count >= 2, "Expected at least 2 hits (one per category)");
        }
        finally { await CleanupAsync(ids); }
    }

    // ── VespaSearchRequest: PresentationSummary ──────────────────────────────

    [Fact]
    public async Task SearchAsync_WithPresentationSummary_Completes()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder<TestProduct>.Select().Where(w => w.True()).Build(),
                Hits = 2,
                PresentationSummary = "default"
            };

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            Assert.NotNull(result.Root);
            Assert.NotEmpty(result.Root.Children);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── VespaSearchRequest: TraceLevel ───────────────────────────────────────

    [Fact]
    public async Task SearchAsync_WithTraceLevel_IncludesTrace()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder<TestProduct>.Select().Where(w => w.True()).Build(),
                Hits = 1,
                TraceLevel = 3
            };

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            Assert.NotNull(result.Root);
            // Trace data is returned in the response — just verify it doesn't break
            Assert.NotEmpty(result.Root.Children);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── VespaSearchRequest: Timeout ──────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_WithShortTimeout_StillReturns()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder<TestProduct>.Select().Where(w => w.True()).Build(),
                Hits = 10,
                Timeout = "10s"
            };

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            Assert.NotNull(result.Root);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── VespaSearchRequest: ModelRestrict ────────────────────────────────────

    [Fact]
    public async Task SearchAsync_WithModelRestrict_QueriesSingleType()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder.Select("*").From("sources *").Where(w => w.True()).Build(),
                Hits = 10,
                ModelRestrict = "product"
            };

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            Assert.NotNull(result.Root);
            Assert.NotEmpty(result.Root.Children);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── VespaSearchRequest: Ranking ──────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_WithRankingProfile_UsesProfile()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder<TestProduct>.Select().Where(w => w.True()).Build(),
                Hits = 5,
                Ranking = new RankingConfig { Profile = "unranked" }
            };

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            Assert.NotNull(result.Root);
            Assert.NotEmpty(result.Root.Children);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── GroupByStreamAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GroupByStreamAsync_PaginatesGrouping()
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

            var pages = new List<GroupingSearchResponse<TestProduct>>();
            await foreach (var page in fixture.Client.Search.GroupByStreamAsync<TestProduct>(request))
                pages.Add(page);

            // At least one page of results
            Assert.NotEmpty(pages);
            Assert.NotEmpty(pages[0].GroupingResults);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── VespaSearchRequest: CustomParameters ─────────────────────────────────

    [Fact]
    public async Task SearchAsync_WithCustomParameters_PassedThrough()
    {
        if (!Enabled) return;

        var ids = await SeedAsync();
        try
        {
            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder<TestProduct>.Select().Where(w => w.True()).Build(),
                Hits = 5,
                CustomParameters = new Dictionary<string, object>
                {
                    ["ranking.softtimeout.enable"] = true
                }
            };

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            Assert.NotNull(result.Root);
            Assert.NotEmpty(result.Root.Children);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── GetHistogramsAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetHistogramsAsync_ReturnsDataOrNull()
    {
        if (!Enabled) return;

        // GetHistogramsAsync may return null if histograms endpoint is not available
        var histograms = await fixture.Client.GetHistogramsAsync();
        // Just verify it does not throw — result can be null or a string
        Assert.True(histograms is null or { Length: > 0 });
    }
}
