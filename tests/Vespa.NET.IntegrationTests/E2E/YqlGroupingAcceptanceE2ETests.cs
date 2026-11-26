using System.Text.Json;
using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Models.Schema;
using Vespa.Models.Tensors;
using Vespa.Query;
using Vespa.Search;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

/// <summary>
/// Exercises generated YQL and grouping syntax against Vespa, including features
/// whose behavior differs between server versions.
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
        try
        {
            foreach (var doc in docs)
            {
                var id = NewId();
                ids.Add(id);
                var put = await fixture.Client.Documents.PutAsync(id, doc);
                Assert.True(put.IsSuccess, $"Could not seed product '{id}': {put.StatusCode} {put.Message}");
            }
            await WaitForSeedAsync(QueryByTag(tag).Build(), ids);
            return (ids, tag);
        }
        catch
        {
            await CleanupAsync(ids);
            throw;
        }
    }

    private async Task WaitForSeedAsync(string yql, IReadOnlyCollection<string> ids)
    {
        var expectedIds = ids.ToHashSet(StringComparer.Ordinal);
        string[] visibleIds = [];
        Coverage? coverage = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: ids.Count, cancellationToken: timeout.Token);
                Assert.Empty(result.Root.Errors ?? []);
                coverage = result.Root.Coverage;
                visibleIds = result.Root.Children.Select(hit => hit.Id).ToArray();
                if (coverage is not { Full: false } && visibleIds.Length == ids.Count && expectedIds.SetEquals(visibleIds))
                    return;

                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Assert.Fail($"Products for query '{yql}' were not searchable within 30 seconds. " +
                $"Expected IDs: [{string.Join(", ", ids)}]. Visible IDs: [{string.Join(", ", visibleIds)}]. Coverage: {coverage}");
        }
    }

    private async Task CleanupAsync(List<string> ids)
    {
        foreach (var id in ids)
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    private static YqlBuilder<TestProduct> QueryByTag(string tag) =>
        YqlBuilder<TestProduct>.Select().Where(w => w.Field(p => p.Tag).Contains(tag));

    private static VespaSearchRequest GroupingRequest(string tag, GroupingBuilder grouping) =>
        new()
        {
            Yql = QueryByTag(tag).GroupBy(grouping).Build(),
            Hits = 0
        };

    [Fact]
    public async Task QuotedStrings_RoundTripInYqlAndGrouping()
    {
        if (!Enabled) return;
        const string category = """a"b\c""";
        const string pattern = """^a"b\\c$""";
        var tag = $"escape-{Guid.NewGuid():N}-{category}";
        var ids = new List<string> { NewId(), NewId() };
        try
        {
            var match = new TestProduct { Tag = tag, Category = category };
            var other = match with { Category = """a"bc""" };
            Assert.True((await fixture.Client.Documents.PutAsync(ids[0], match)).IsSuccess);
            Assert.True((await fixture.Client.Documents.PutAsync(ids[1], other)).IsSuccess);
            // The YQL query matches the escaped tag on a string attribute and checks both IDs.
            await WaitForSeedAsync(QueryByTag(tag).Build(), ids);

            var filtered = await fixture.Client.Search.GroupByAsync<TestProduct>(GroupingRequest(tag,
                GroupingBuilder.All().Group("category").Filter(GroupingFilter.Regex(pattern, "category"))
                    .Each(e => e.Output(GroupingAgg.Count()).Summary(maxHits: 1))));
            Assert.Empty(filtered.Errors ?? []);
            var group = Assert.Single(Assert.Single(filtered.GroupingResults).Groups);
            Assert.Equal(1L, group.GetAggregation<long>("count()"));
            Assert.Equal(ids[0], Assert.Single(group.GetHits<TestProduct>()).Id);

            var bucketed = await fixture.Client.Search.GroupByAsync<TestProduct>(GroupingRequest(tag,
                GroupingBuilder.All().GroupByBuckets("category", GroupingBucket.Text(category, category + "z"))
                    .Each(e => e.Output(GroupingAgg.Count()).Summary(maxHits: 1))));
            Assert.Empty(bucketed.Errors ?? []);
            var bucket = Assert.Single(Assert.Single(bucketed.GroupingResults).Groups, g => !g.IsRestBucket);
            Assert.Equal(1L, bucket.GetAggregation<long>("count()"));
            Assert.Equal(ids[0], Assert.Single(bucket.GetHits<TestProduct>()).Id);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Contains_MatchesNewlineAndEscapedNewlineOnAStringAttribute()
    {
        if (!Enabled) return;
        var prefix = $"newline-{Guid.NewGuid():N}-";
        string[] tags = [prefix + "a\nb", prefix + "a b", prefix + @"a\nb"];
        var ids = new List<string> { NewId(), NewId(), NewId() };
        var marker = Random.Shared.Next(1_000_000, int.MaxValue);
        var client = fixture.Client;
        try
        {
            for (var index = 0; index < tags.Length; index++)
                await client.Documents.PutAsync(ids[index], new TestProduct { Tag = tags[index], Quantity = marker });
            await WaitForSeedAsync(YqlBuilder<TestProduct>.Select()
                .Where(w => w.Field(p => p.Quantity).EqualTo(marker)).Build(), ids);

            await AssertMatchAsync(QueryByTag(tags[0]).Build(), 0);
            await AssertMatchAsync($"select * from product where tag contains \"{prefix}a\\nb\"", 0);
            await AssertMatchAsync(QueryByTag(tags[1]).Build(), 1);
            await AssertMatchAsync(QueryByTag(tags[2]).Build(), 2);
        }
        finally { await CleanupAsync(ids); }

        async Task AssertMatchAsync(string yql, int index)
        {
            var result = await client.Search.QueryAsync<TestProduct>(yql);
            Assert.Empty(result.Root.Errors ?? []);
            Assert.True(result.Root.Coverage?.Full);
            var hit = Assert.Single(result.Root.Children);
            Assert.Equal(ids[index], hit.Id);
            Assert.Equal(tags[index], hit.Fields!.Tag);
        }
    }

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
    public async Task GroupingFilter_IsTrue_RequiresBooleanGroupingSupport()
    {
        if (!Enabled) return;
        var (ids, tag) = await SeedAsync();
        try
        {
            var request = GroupingRequest(tag, GroupingBuilder.All()
                .Group("category")
                .Filter(GroupingFilter.IsTrue("in_stock"))
                .Each(e => e.Output(GroupingAgg.Count())));

            if (await ServerVersionAsync() < new Version(8, 646, 22))
            {
                var error = await Assert.ThrowsAsync<VespaException>(() =>
                    fixture.Client.Search.GroupByAsync<TestProduct>(request));
                Assert.Equal(400, error.StatusCode);
                Assert.Contains("Encountered", error.Message);
                Assert.Contains("istrue", error.Message);
                return;
            }

            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(request);
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

            Assert.Empty(result.Errors ?? []);
            Assert.True(result.Coverage is not { Full: false }, $"Grouping coverage: {result.Coverage}");
            Assert.True(result.GroupingResults.Count == 1,
                $"Expected one group list for '{tag}', got {result.GroupingResults.Count} with {result.TotalCount} matching documents.");
            var group = Assert.Single(result.GroupingResults[0].Groups);
            Assert.Equal($"{tag}-beta", group.Value);
            Assert.Equal(2, group.GetAggregation<long>("count()"));
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
            var composite = GroupingFilter
                .Not(GroupingFilter.Regex(".*-gamma", "category"))
                .And(GroupingFilter.Range(0, 30, "quantity").Or(GroupingFilter.Regex(".*-beta", "category")));

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
            request.TraceLevel = 5;

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(request);
            Assert.NotEmpty(result.Root.Children);
            Assert.Contains("totalTargetHits: 5", ParsedYql(result));
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

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(new() { Yql = yql, Hits = 10, TraceLevel = 5 });
            Assert.NotEmpty(result.Root.Children);
            Assert.Contains("totalTargetHits: 10", ParsedYql(result));
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task Wand_TotalTargetHits_IsAppliedOnSupportedServers()
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

            var result = await fixture.Client.Search.SearchAsync<TestProduct>(new() { Yql = yql, Hits = 10, TraceLevel = 5 });
            Assert.NotEmpty(result.Root.Children);
            var parsed = ParsedYql(result);
            if (await ServerVersionAsync() < new Version(8, 664, 22))
                Assert.DoesNotContain("totalTargetHits", parsed);
            else
                Assert.Contains("totalTargetHits: 10", parsed);
        }
        finally { await CleanupAsync(ids); }
    }

    private async Task<Version> ServerVersionAsync()
    {
        var reportedVersion = await fixture.Client.GetVersionAsync();
        Assert.True(Version.TryParse(reportedVersion, out var version), $"Unexpected Vespa version: '{reportedVersion}'.");
        return version;
    }

    private static string ParsedYql(VespaSearchResponse<TestProduct> result)
    {
        var messages = result.Trace?.Children?.OfType<JsonElement>().SelectMany(TraceMessages) ?? [];
        return Assert.Single(messages, message => message.StartsWith("YQL query parsed:", StringComparison.Ordinal));
    }

    private static IEnumerable<string> TraceMessages(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
            yield break;
        if (entry.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            yield return message.GetString()!;
        if (entry.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                foreach (var text in TraceMessages(child))
                    yield return text;
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
                    .ContainsAnnotated(term, new() { Weight = 200, Ranked = true, Stem = false }))
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
    public async Task TermAnnotations_ConnectedTermsCanCarryTheirOwnIds()
    {
        if (!Enabled) return;

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var id = NewId();
        var term = $"annot{Guid.NewGuid():N}";
        try
        {
            var put = await fixture.Client.Documents.PutAsync(id,
                new TestProduct { Name = "annotated", Description = $"{term} wireless headphones" },
                cancellationToken: token);
            Assert.True(put.IsSuccess);

            var request = YqlBuilder<TestProduct>.Select().Where(w =>
            {
                var description = w.Field(p => p.Description);
                description.ContainsAnnotated(term, new() { TermId = 1, Connectivity = new(2, 0.8) });
                description.ContainsAnnotated("wireless", new() { TermId = 2, Connectivity = new(3, 0.6) });
                description.ContainsAnnotated("headphones", new() { TermId = 3 });
            }).ToSearchRequest();

            while (true)
            {
                var result = await fixture.Client.Search.SearchAsync<TestProduct>(request, token);
                Assert.Empty(result.Root.Errors ?? []);
                if (result.Root.Children.Count > 0)
                {
                    Assert.Equal(id, Assert.Single(result.Root.Children).Id);
                    break;
                }
                await Task.Delay(200, token);
            }
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
                .Where(w => w.Field(p => p.Tag).ContainsAnnotated(tag[..12], new() { Prefix = true }))
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
                .Where(w => w.Field("event_name").ContainsAnnotated("suffixdoc", new() { Suffix = true }))
                .ToSearchRequest()
                .WithTimeout(TimeSpan.FromSeconds(10));
            suffixRequest.StreamingGroupName = group;
            var suffixResult = await fixture.Client.Search.SearchAsync<TestEvent>(suffixRequest);
            Assert.NotNull(suffixResult.Root);

            var substringRequest = YqlBuilder.Select().From("event")
                .Where(w => w.Field("event_name").ContainsAnnotated("ingsuffix", new() { Substring = true }))
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
            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(
                GroupingRequest(tag, GroupingBuilder.All()
                    .GroupByBuckets("price",
                        GroupingBucket.Numeric(null, 100),
                        GroupingBucket.Numeric(100, 300, toInclusive: true),
                        GroupingBucket.Numeric(300, null, fromInclusive: false))
                    .Each(e => e.Output(GroupingAgg.Count()))));

            var groups = Assert.Single(result.GroupingResults).Groups;
            Assert.DoesNotContain(groups, group => group.IsRestBucket);
            Assert.Equal([1L, 1L, 2L], groups.Select(group => group.GetAggregation<long>("count()")).Order());
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

        var profilePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(profilePath, """
                <query-profile id="unbounded-grouping">
                    <field name="grouping.globalMaxGroups">-1</field>
                </query-profile>
                """);
            await fixture.Client.Admin.DeploySchemaAsync(VespaFixture.SchemaTypes,
                options: new ApplicationPackageOptions
                {
                    AdditionalFiles = new Dictionary<string, string>
                    {
                        ["search/query-profiles/unbounded-grouping.xml"] = profilePath
                    }
                });
        }
        finally { File.Delete(profilePath); }

        // 12 distinct categories exceed Vespa's default group count of 10.
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

            var request = GroupingRequest(tag, GroupingBuilder.All()
                .Group("category")
                .MaxAll()
                .Each(e => e.Output(GroupingAgg.Count())));
            request.QueryProfile = "unbounded-grouping";

            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(request);

            var groups = Assert.Single(result.GroupingResults).Groups;
            Assert.Equal(categoryCount, groups.Count);
        }
        finally { await CleanupAsync(ids); }
    }
}
