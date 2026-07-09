using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Models.Tensors;
using Vespa.Query;
using Vespa.Search;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

/// <summary>
/// Acceptance tests for the newly expressible YQL and grouping syntax: each test
/// runs a real query and asserts Vespa parses and executes it (no VespaException),
/// not full ranking semantics. Mock-based unit tests cannot catch
/// plausible-but-wrong syntax; these do.
/// </summary>
[Collection("Vespa")]
[Trait("Category", "E2E")]
public class YqlGroupingAcceptanceE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    private static string NewId() => $"yqlacc-{Guid.NewGuid():N}";

    private async Task<(List<string> Ids, string Tag)> SeedAsync()
    {
        var tag = $"acc-{Guid.NewGuid():N}";
        var docs = new[]
        {
            new TestProduct { Name = "Alpha One", Price = 50, Category = $"{tag}-alpha", Quantity = 10, InStock = true, Tag = tag,
                Embedding = VespaTensor.FromDenseValues(new float[] { 1, 0, 0, 0 }),
                TermWeights = new Dictionary<string, int> { ["red"] = 10, ["blue"] = 5 } },
            new TestProduct { Name = "Alpha Two", Price = 150, Category = $"{tag}-alpha", Quantity = 25, InStock = false, Tag = tag,
                Embedding = VespaTensor.FromDenseValues(new float[] { 0, 1, 0, 0 }),
                TermWeights = new Dictionary<string, int> { ["red"] = 2 } },
            new TestProduct { Name = "Beta One", Price = 300, Category = $"{tag}-beta", Quantity = 40, InStock = true, Tag = tag,
                Embedding = VespaTensor.FromDenseValues(new float[] { 0, 0, 1, 0 }),
                TermWeights = new Dictionary<string, int> { ["green"] = 7 } },
            new TestProduct { Name = "Beta Two", Price = 700, Category = $"{tag}-beta", Quantity = 60, InStock = true, Tag = tag,
                Embedding = VespaTensor.FromDenseValues(new float[] { 0, 0, 0, 1 }) },
        };

        var ids = new List<string>();
        foreach (var doc in docs)
        {
            var id = NewId();
            ids.Add(id);
            await fixture.Client.Documents.PutAsync(id, doc);
        }
        await Task.Delay(2000);
        return (ids, tag);
    }

    private async Task CleanupAsync(List<string> ids)
    {
        foreach (var id in ids)
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    private VespaSearchRequest GroupingRequest(string tag, GroupingBuilder grouping) =>
        new()
        {
            Yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w.Field(p => p.Tag).Contains(tag))
                .GroupBy(grouping)
                .Build(),
            Hits = 0
        };

    // ── Grouping filter / keep ───────────────────────────────────────────────

    [Fact]
    public async Task GroupingFilter_Regex_RestrictsGroups()
    {
        if (!Enabled) return;
        var (ids, tag) = await SeedAsync();
        try
        {
            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(
                GroupingRequest(tag, GroupingBuilder.All()
                    .Group("category")
                    .Filter(GroupingFilter.Regex(".*-alpha", "category"))
                    .Each(e => e.Output(GroupingAgg.Count()))));

            var groups = Assert.Single(result.GroupingResults).Groups;
            Assert.NotEmpty(groups);
            Assert.All(groups, g => Assert.EndsWith("-alpha", g.Value));
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task GroupingFilter_Range_Accepted()
    {
        if (!Enabled) return;
        var (ids, tag) = await SeedAsync();
        try
        {
            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(
                GroupingRequest(tag, GroupingBuilder.All()
                    .Group("quantity")
                    .Filter(GroupingFilter.Range(0, 50, "quantity"))
                    .Each(e => e.Output(GroupingAgg.Count()))));

            Assert.NotEmpty(result.GroupingResults);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task GroupingFilter_IsTrue_Accepted()
    {
        if (!Enabled) return;
        var (ids, tag) = await SeedAsync();
        try
        {
            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(
                GroupingRequest(tag, GroupingBuilder.All()
                    .Group("category")
                    .Filter(GroupingFilter.IsTrue("in_stock"))
                    .Each(e => e.Output(GroupingAgg.Count()))));

            Assert.NotEmpty(result.GroupingResults);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task GroupingKeep_Regex_Accepted()
    {
        if (!Enabled) return;
        var (ids, tag) = await SeedAsync();
        try
        {
            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(
                GroupingRequest(tag, GroupingBuilder.All()
                    .Group("category")
                    .Keep(GroupingFilter.Regex(".*-beta", "category"))
                    .Each(e => e.Output(GroupingAgg.Count()))));

            var groups = Assert.Single(result.GroupingResults).Groups;
            Assert.All(groups, g => Assert.EndsWith("-beta", g.Value));
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task GroupingFilter_NotAndOrComposition_Accepted()
    {
        if (!Enabled) return;
        var (ids, tag) = await SeedAsync();
        try
        {
            // not (regex) and (range or istrue)
            var composite = GroupingFilter
                .Not(GroupingFilter.Regex(".*-gamma", "category"))
                .And(GroupingFilter.Range(0, 100, "quantity").Or(GroupingFilter.IsTrue("in_stock")));

            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(
                GroupingRequest(tag, GroupingBuilder.All()
                    .Group("category")
                    .Filter(composite)
                    .Each(e => e.Output(GroupingAgg.Count()))));

            Assert.NotEmpty(result.GroupingResults);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── Where-clause negation ────────────────────────────────────────────────

    [Fact]
    public async Task Not_Predicate_ExcludesMatchingDocuments()
    {
        if (!Enabled) return;
        var (ids, tag) = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w
                    .Field(p => p.Tag).Contains(tag)
                    .Not(n => n.Field("category").Contains($"{tag}-alpha")))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.Equal(2, result.Root.Children.Count); // only the beta documents
            Assert.All(result.Root.Children, h => Assert.EndsWith("-beta", h.Fields?.Category));
        }
        finally { await CleanupAsync(ids); }
    }

    // ── near / onear with excluded terms ─────────────────────────────────────

    [Fact]
    public async Task Near_WithExcludedTerms_Accepted()
    {
        if (!Enabled) return;

        var id = NewId();
        var (t1, t2, t3) = ($"w{Guid.NewGuid():N}", $"x{Guid.NewGuid():N}", $"y{Guid.NewGuid():N}");
        try
        {
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = "near doc",
                Category = "near",
                Description = $"{t1} {t2} {t3}"
            });
            await Task.Delay(2000);

            // Positive terms only → matches
            var plain = YqlBuilder<TestProduct>.Select()
                .Where(w => w.Field(p => p.Description).Near([t1, t3], distance: 5))
                .Build();
            var plainResult = await fixture.Client.Search.QueryAsync<TestProduct>(plain);
            Assert.Single(plainResult.Root.Children);

            // Negated term sits between them → excluded, no match
            var excluded = YqlBuilder<TestProduct>.Select()
                .Where(w => w.Field(p => p.Description).Near(
                    [t1, t3], distance: 5, excludedTerms: [t2], exclusionDistance: 3))
                .Build();
            var excludedResult = await fixture.Client.Search.QueryAsync<TestProduct>(excluded);
            Assert.Empty(excludedResult.Root.Children);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task ONear_WithExcludedTerms_Accepted()
    {
        if (!Enabled) return;

        var id = NewId();
        var (t1, t2, t3) = ($"w{Guid.NewGuid():N}", $"x{Guid.NewGuid():N}", $"y{Guid.NewGuid():N}");
        try
        {
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = "onear doc",
                Category = "onear",
                Description = $"{t1} {t2} {t3}"
            });
            await Task.Delay(2000);

            var ordered = YqlBuilder<TestProduct>.Select()
                .Where(w => w.Field(p => p.Description).ONear([t1, t3], distance: 5))
                .Build();
            var orderedResult = await fixture.Client.Search.QueryAsync<TestProduct>(ordered);
            Assert.Single(orderedResult.Root.Children);

            var excluded = YqlBuilder<TestProduct>.Select()
                .Where(w => w.Field(p => p.Description).ONear(
                    [t1, t3], distance: 5, excludedTerms: [t2]))
                .Build();
            var excludedResult = await fixture.Client.Search.QueryAsync<TestProduct>(excluded);
            Assert.Empty(excludedResult.Root.Children);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    // ── totalTargetHits annotations ──────────────────────────────────────────

    [Fact]
    public async Task NearestNeighbor_TotalTargetHits_Accepted()
    {
        if (!Enabled) return;
        var (ids, tag) = await SeedAsync();
        try
        {
            var request = YqlBuilder<TestProduct>.Select()
                .Where(w => w
                    .Field(p => p.Tag).Contains(tag)
                    .NearestNeighbor("embedding", "query_embedding", totalTargetHits: 5))
                .ToSearchRequest()
                .WithRankProfile("closeness_profile")
                .WithQueryTensor("query_embedding", new float[] { 1, 0, 0, 0 });

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            Assert.NotEmpty(result.Root.Children);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task WeakAnd_TotalTargetHits_Accepted()
    {
        if (!Enabled) return;
        var (ids, tag) = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w
                    .Field(p => p.Tag).Contains(tag)
                    .WeakAnd(wa =>
                    {
                        wa.Field("product_name").Contains("Alpha");
                        wa.Field("product_name").Contains("One");
                    }, totalTargetHits: 10))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.NotEmpty(result.Root.Children);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Wand_TotalTargetHits_Accepted()
    {
        if (!Enabled) return;
        var (ids, tag) = await SeedAsync();
        try
        {
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w
                    .Field(p => p.Tag).Contains(tag)
                    .Wand("term_weights",
                        new Dictionary<string, int> { ["red"] = 3, ["green"] = 1 },
                        totalTargetHits: 10))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
            Assert.NotEmpty(result.Root.Children); // red/green docs match
        }
        finally { await CleanupAsync(ids); }
    }

    // ── Term annotations ─────────────────────────────────────────────────────

    [Fact]
    public async Task TermAnnotations_WeightAndRanked_Accepted()
    {
        if (!Enabled) return;

        var id = NewId();
        var term = $"annot{Guid.NewGuid():N}";
        try
        {
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = "annotated",
                Category = "annot",
                Description = $"leading {term} trailing"
            });
            await Task.Delay(2000);

            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w.Field(p => p.Description)
                    .ContainsAnnotated(term, weight: 200, ranked: true, stem: false))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql);
            Assert.Single(result.Root.Children);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task TermAnnotation_Prefix_MatchesOnAttributeField()
    {
        if (!Enabled) return;

        var id = NewId();
        var tag = $"prefixable{Guid.NewGuid():N}";
        try
        {
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = "prefix doc",
                Category = "prefix",
                Tag = tag
            });
            await Task.Delay(2000);

            // tag is a (non-indexed) string attribute — prefix matching is supported there
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w.Field(p => p.Tag).ContainsAnnotated(tag[..12], prefix: true))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql);
            Assert.Single(result.Root.Children);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task TermAnnotations_SubstringSuffix_AcceptedInStreamingSearch()
    {
        if (!Enabled) return;

        // substring/suffix annotations are streaming-search features. Probing this
        // Vespa 8 image showed that in a MIXED index+streaming content cluster (as
        // deployed by the shared fixture) the annotations parse and execute (HTTP 200)
        // but only take matching effect in streaming-only clusters — so this test
        // asserts acceptance, not matching. Substring matching in indexed mode is
        // covered by GramMatchField_SubstringQuery_Matches.
        var group = $"grp-{Guid.NewGuid():N}";
        var lid = NewId();
        try
        {
            await fixture.Client.Documents.PutByGroupAsync(
                group, lid,
                new TestEvent { EventName = "streamingsuffixdoc", Payload = "p" },
                "event", @namespace: "test");
            await Task.Delay(2000);

            // Cold streaming visits routinely exceed Vespa's 500 ms default query timeout
            var suffixRequest = YqlBuilder.Select().From("event")
                .Where(w => w.Field("event_name").ContainsAnnotated("suffixdoc", suffix: true))
                .ToSearchRequest()
                .WithTimeout(TimeSpan.FromSeconds(10));
            suffixRequest.StreamingGroupName = group;
            var suffixResult = await fixture.Client.Search.SearchAsync<TestEvent>(suffixRequest);
            Assert.NotNull(suffixResult.Root);

            var substringRequest = YqlBuilder.Select().From("event")
                .Where(w => w.Field("event_name").ContainsAnnotated("ingsuffix", substring: true))
                .ToSearchRequest()
                .WithTimeout(TimeSpan.FromSeconds(10));
            substringRequest.StreamingGroupName = group;
            var substringResult = await fixture.Client.Search.SearchAsync<TestEvent>(substringRequest);
            Assert.NotNull(substringResult.Root);
        }
        finally
        {
            await fixture.Client.Documents.DeleteByGroupAsync(group, lid, "event", @namespace: "test");
        }
    }

    [Fact]
    public async Task GramMatchField_SubstringQuery_Matches()
    {
        if (!Enabled) return;

        var id = NewId();
        var text = $"zqx{Guid.NewGuid():N}vbn";
        try
        {
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = "gram doc",
                Category = "gram",
                GramText = text
            });
            await Task.Delay(2000);

            // match: gram indexes n-grams, so an inner substring of the stored value matches
            var yql = YqlBuilder<TestProduct>.Select()
                .Where(w => w.Field(p => p.GramText).Contains(text.Substring(5, 10)))
                .Build();

            var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql);
            Assert.Single(result.Root.Children);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    // ── in (@param) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task InParameter_ResolvesValuesFromRequestParameter()
    {
        if (!Enabled) return;
        var (ids, tag) = await SeedAsync();
        try
        {
            var request = YqlBuilder<TestProduct>.Select()
                .Where(w => w
                    .Field(p => p.Tag).Contains(tag)
                    .Field("quantity").InParameter("wanted_quantities"))
                .ToSearchRequest()
                .WithParameter("wanted_quantities", "10,40");

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            Assert.Equal(2, result.Root.Children.Count);
            Assert.All(result.Root.Children, h => Assert.True(h.Fields?.Quantity is 10 or 40));
        }
        finally { await CleanupAsync(ids); }
    }

    // ── Buckets with inf endpoints and open/closed bounds ────────────────────

    [Fact]
    public async Task GroupingBuckets_InfEndpointsAndClosedBounds_Accepted()
    {
        if (!Enabled) return;
        var (ids, tag) = await SeedAsync();
        try
        {
            // Buckets must stay strictly monotonic after Vespa's normalization
            // ([100,500] becomes [100,501>), so the open-from bucket starts at 600.
            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(
                GroupingRequest(tag, GroupingBuilder.All()
                    .GroupByBuckets("price",
                        GroupingBucket.Numeric(null, 100),                     // bucket[-inf,100)
                        GroupingBucket.Numeric(100, 500, toInclusive: true),   // bucket[100,500]
                        GroupingBucket.Numeric(600, null, fromInclusive: false)) // bucket(600,inf)
                    .Each(e => e.Output(GroupingAgg.Count()))));

            var groups = Assert.Single(result.GroupingResults).Groups;
            // Prices 50, 150, 300, 700 → every bucket collects at least one document
            Assert.True(groups.Count >= 3, $"expected >= 3 buckets, got {groups.Count}");
        }
        finally { await CleanupAsync(ids); }
    }

    // ── as(label) outputs and parallel each blocks ───────────────────────────

    [Fact]
    public async Task GroupingOutput_AsLabel_RenamesAggregation()
    {
        if (!Enabled) return;
        var (ids, tag) = await SeedAsync();
        try
        {
            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(
                GroupingRequest(tag, GroupingBuilder.All()
                    .Group("category")
                    .Each(e => e.Output(GroupingAgg.As(GroupingAgg.Count(), "n")))));

            var groups = Assert.Single(result.GroupingResults).Groups;
            Assert.NotEmpty(groups);
            Assert.All(groups, g => Assert.Contains("n", g.RawAggregations.Keys));
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Grouping_ParallelEachBlocks_WithLabels_Accepted()
    {
        if (!Enabled) return;
        var (ids, tag) = await SeedAsync();
        try
        {
            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(
                GroupingRequest(tag, GroupingBuilder.All()
                    .Group("category")
                    .Each(e => e.Output(GroupingAgg.Count()).As("counts"))
                    .Each(e => e.Output(GroupingAgg.Avg("price")).As("prices"))));

            Assert.NotEmpty(result.GroupingResults);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── Multi-key order and max(inf) ─────────────────────────────────────────

    [Fact]
    public async Task Grouping_MultiKeyOrder_Accepted()
    {
        if (!Enabled) return;
        var (ids, tag) = await SeedAsync();
        try
        {
            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(
                GroupingRequest(tag, GroupingBuilder.All()
                    .Group("category")
                    .Max(10)
                    .Precision(100)
                    .OrderByDescending(GroupingAgg.Count())
                    .OrderByAscending(GroupingAgg.Avg("price"))
                    .Each(e => e.Output(GroupingAgg.Count(), GroupingAgg.Avg("price")))));

            var groups = Assert.Single(result.GroupingResults).Groups;
            Assert.Equal(2, groups.Count);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Grouping_MaxInf_ReturnsAllGroups()
    {
        if (!Enabled) return;

        // 12 distinct categories exceed Vespa's default group cap of 10
        var tag = $"maxinf-{Guid.NewGuid():N}";
        const int categoryCount = 12;
        var ids = new List<string>();
        try
        {
            for (var i = 0; i < categoryCount; i++)
            {
                var id = NewId();
                ids.Add(id);
                await fixture.Client.Documents.PutAsync(id, new TestProduct
                {
                    Name = $"MaxInf {i}",
                    Price = i,
                    Category = $"{tag}-cat-{i:D2}",
                    Tag = tag
                });
            }
            await Task.Delay(2000);

            // max(inf) is rejected while grouping.globalMaxGroups is enabled —
            // Vespa requires disabling the global cap for unbounded group counts.
            var request = GroupingRequest(tag, GroupingBuilder.All()
                .Group("category")
                .MaxAll()
                .Each(e => e.Output(GroupingAgg.Count())));
            request.GroupingGlobalMaxGroups = -1;

            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(request);

            var groups = Assert.Single(result.GroupingResults).Groups;
            Assert.Equal(categoryCount, groups.Count);
        }
        finally { await CleanupAsync(ids); }
    }
}
