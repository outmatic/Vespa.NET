using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Models.Tensors;
using Vespa.Query;
using Vespa.Search;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public class SearchAdvancedE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    private static string NewId() => $"adv-{Guid.NewGuid():N}";

    // ── Nearest Neighbor ─────────────────────────────────────────────────────

    [Fact]
    public async Task NearestNeighborSearch_ReturnsSimilarDocuments()
    {
        if (!Enabled) return;

        var ids = new List<string>();
        try
        {
            // Seed 3 docs with known embeddings
            var docs = new[]
            {
                new TestProduct { Name = "NN-A", Price = 10, Category = "nn", Embedding = VespaTensor.FromDenseValues(new float[] { 1, 0, 0, 0 }) },
                new TestProduct { Name = "NN-B", Price = 20, Category = "nn", Embedding = VespaTensor.FromDenseValues(new float[] { 0, 1, 0, 0 }) },
                new TestProduct { Name = "NN-C", Price = 30, Category = "nn", Embedding = VespaTensor.FromDenseValues(new float[] { 1, 0.1f, 0, 0 }) },
            };

            foreach (var doc in docs)
            {
                var id = NewId();
                ids.Add(id);
                await fixture.Client.Documents.PutAsync(id, doc);
            }
            await Task.Delay(2000);

            // Query with embedding close to [1,0,0,0] — should return NN-A and NN-C as top hits
            var queryTensor = VespaTensor.FromDenseValues(new float[] { 1, 0, 0, 0 });
            var result = await fixture.Client.Search.NearestNeighborSearchAsync<TestProduct>(
                queryTensor, "embedding", "product",
                topK: 3, rankProfile: "closeness_profile", @namespace: "test");

            Assert.NotEmpty(result.Root.Children);
            // The closest document should be NN-A (exact match)
            Assert.Contains(result.Root.Children, h => h.Fields?.Name == "NN-A");
        }
        finally
        {
            foreach (var id in ids)
                await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task NearestNeighborSearch_TypedExtension_Works()
    {
        if (!Enabled) return;

        var ids = new List<string>();
        try
        {
            var id = NewId();
            ids.Add(id);
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = "NN-Typed",
                Price = 5,
                Category = "nn",
                Embedding = VespaTensor.FromDenseValues(new float[] { 0, 0, 1, 0 })
            });
            await Task.Delay(2000);

            var queryTensor = VespaTensor.FromDenseValues(new float[] { 0, 0, 1, 0 });
            var result = await fixture.Client.Search.NearestNeighborSearchAsync<TestProduct>(
                queryTensor, p => p.Embedding!,
                topK: 5, rankProfile: "closeness_profile");

            Assert.NotEmpty(result.Root.Children);
            Assert.Contains(result.Root.Children, h => h.Fields?.Name == "NN-Typed");
        }
        finally
        {
            foreach (var id in ids)
                await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    // ── WeakAnd ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task WeakAnd_ReturnsPartialMatches()
    {
        if (!Enabled) return;

        var ids = new List<string>();
        try
        {
            var products = new[]
            {
                new TestProduct { Name = "Wireless Mouse",      Price = 25, Category = "weakand" },
                new TestProduct { Name = "Wireless Keyboard",   Price = 75, Category = "weakand" },
                new TestProduct { Name = "Wired Mouse",         Price = 15, Category = "weakand" },
            };

            foreach (var p in products)
            {
                var id = NewId();
                ids.Add(id);
                await fixture.Client.Documents.PutAsync(id, p);
            }
            await Task.Delay(2000);

            var yql = YqlBuilder<TestProduct>
                .Select()
                .Where(w => w.WeakAnd(wa =>
                {
                    wa.Field(p => p.Name).Contains("Wireless");
                    wa.Field(p => p.Name).Contains("Mouse");
                }))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.NotEmpty(result.Root.Children);
            // Should match all 3 since weakAnd returns docs matching at least one term
            Assert.True(result.Root.Children.Count >= 1);
        }
        finally
        {
            foreach (var id in ids)
                await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    // ── Fuzzy search ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Fuzzy_MatchesApproximateTerms()
    {
        if (!Enabled) return;

        var ids = new List<string>();
        try
        {
            var id = NewId();
            ids.Add(id);
            // Fuzzy requires a pure string attribute field — use tag (AttributeSummary, no index)
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = "Bluetooth Headphones",
                Price = 80,
                Category = "fuzzy",
                Tag = "bluetooth"
            });
            await Task.Delay(2000);

            // "bluetoth" is a typo for "bluetooth" — fuzzy should match on attribute field
            var yql = YqlBuilder<TestProduct>
                .Select()
                .Where(w => w.Field(p => p.Tag).Fuzzy("bluetoth", maxEditDistance: 2))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.NotEmpty(result.Root.Children);
            Assert.Contains(result.Root.Children, h => h.Fields?.Tag == "bluetooth");
        }
        finally
        {
            foreach (var id in ids)
                await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    // ── SearchStreamAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task SearchStreamAsync_StreamsIndividualHits()
    {
        if (!Enabled) return;

        var ids = new List<string>();
        try
        {
            // Seed 6 documents
            for (int i = 0; i < 6; i++)
            {
                var id = NewId();
                ids.Add(id);
                await fixture.Client.Documents.PutAsync(id, new TestProduct
                {
                    Name = $"Stream-{i}",
                    Price = i * 10,
                    Category = "stream-e2e"
                });
            }
            await Task.Delay(2000);

            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder<TestProduct>
                    .Select()
                    .Where(w => w.Field(p => p.Category).Contains("stream-e2e"))
                    .Build()
            };

            var hits = new List<SearchHit<TestProduct>>();
            await foreach (var hit in fixture.Client.Search.SearchStreamAsync<TestProduct>(request, pageSize: 2))
                hits.Add(hit);

            Assert.Equal(6, hits.Count);
            Assert.Equal(hits.Count, hits.Select(h => h.Id).Distinct().Count()); // no duplicates
        }
        finally
        {
            foreach (var id in ids)
                await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    // ── Nested grouping ──────────────────────────────────────────────────────

    [Fact]
    public async Task NestedGrouping_GroupsWithinGroups()
    {
        if (!Enabled) return;

        var ids = new List<string>();
        try
        {
            var products = new[]
            {
                new TestProduct { Name = "NG-1", Price = 10, Category = "cat-a", InStock = true },
                new TestProduct { Name = "NG-2", Price = 20, Category = "cat-a", InStock = false },
                new TestProduct { Name = "NG-3", Price = 30, Category = "cat-b", InStock = true },
                new TestProduct { Name = "NG-4", Price = 40, Category = "cat-b", InStock = true },
            };

            foreach (var p in products)
            {
                var id = NewId();
                ids.Add(id);
                await fixture.Client.Documents.PutAsync(id, p);
            }
            await Task.Delay(2000);

            // Group by category, then within each category group by in_stock
            var request = new VespaSearchRequest
            {
                Yql = YqlBuilder<TestProduct>
                    .Select()
                    .GroupBy(
                        GroupingBuilder.All()
                            .Group("category")
                            .Max(10)
                            .Each(e => e
                                .Output(GroupingAgg.Count())
                                .SubGroup(
                                    GroupingBuilder.All()
                                        .Group("in_stock")
                                        .Each(inner => inner.Output(GroupingAgg.Count())))))
                    .Build(),
                Hits = 0
            };

            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(request);
            Assert.NotEmpty(result.GroupingResults);

            var topGroups = result.GroupingResults[0].Groups;
            Assert.True(topGroups.Count >= 2); // cat-a and cat-b

            // At least one group should have sub-groups
            var withSubs = topGroups.Where(g => g.SubGroups.Count > 0).ToList();
            Assert.NotEmpty(withSubs);
        }
        finally
        {
            foreach (var id in ids)
                await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }
}
