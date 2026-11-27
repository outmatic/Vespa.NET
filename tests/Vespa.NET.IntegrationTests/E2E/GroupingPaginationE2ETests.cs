using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Query;
using Vespa.Search;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

/// <summary>
/// E2E coverage for the grouping paths that only unit tests covered before:
/// continuation-based pagination (YQL <c>continuations</c> annotation) and
/// per-group document summaries (<c>each(output(summary()))</c> → <c>hitlist:*</c>).
/// </summary>
[Collection("Vespa")]
[Trait("Category", "E2E")]
public class GroupingPaginationE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    private static string NewId() => $"grp-{Guid.NewGuid():N}";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupByStreamAsync_ManyGroups_PaginatesWithContinuationTokens(bool useSelect)
    {
        if (!Enabled) return;

        var runTag = $"page-{Guid.NewGuid():N}";
        const int groupCount = 30;
        var ids = new List<string>();
        try
        {
            for (var i = 0; i < groupCount; i++)
            {
                var id = NewId();
                ids.Add(id);
                await fixture.Client.Documents.PutAsync(id, new TestProduct
                {
                    Name = $"Paged {i}",
                    Price = i,
                    Category = $"{runTag}-cat-{i:D2}",
                    Tag = runTag
                });
            }
            await Task.Delay(3000);

            // max(5) groups per page over 30 distinct categories forces continuations
            var builder = YqlBuilder.Select().From("product")
                .Where(w => w.Field("tag").Contains(runTag))
                .Limit(0);
            var grouping = GroupingBuilder.All()
                .Group("category")
                .Max(5)
                .OrderByAscending(GroupingAgg.Min("price"))
                .Each(e => e.Output(GroupingAgg.Count()));
            var request = useSelect ? builder.ToSearchRequest() : builder.GroupBy(grouping).ToSearchRequest();
            if (useSelect)
            {
                request = request with { Select = grouping.Build() };
                var first = await fixture.Client.Search.GroupByAsync<TestProduct>(request);
                Assert.Equal(groupCount, first.TotalCount);
                Assert.NotNull(first.Coverage);
                var firstGroups = Assert.Single(first.GroupingResults).Groups;
                var tokens = Assert.IsAssignableFrom<IReadOnlyList<string>>(first.ContinuationTokens);
                var resume = request.WithParameter("continue", string.Join(' ', tokens));
                var second = await fixture.Client.Search.GroupByAsync<TestProduct>(resume);
                var secondGroups = Assert.Single(second.GroupingResults).Groups;
                Assert.Equal(5, firstGroups.Count);
                Assert.Equal(5, secondGroups.Count);
                Assert.Empty(firstGroups.Select(g => g.Value).Intersect(secondGroups.Select(g => g.Value)));
            }

            var pages = 0;
            var seenGroups = new List<string>();
            await foreach (var response in fixture.Client.Search.GroupByStreamAsync<TestProduct>(request))
            {
                var page = response.AsTyped();
                pages++;
                foreach (var list in page.GroupingResults)
                {
                    seenGroups.AddRange(list.Groups.Select(g => g.Value));
                    Assert.All(list.Groups, group => Assert.Equal(1, group.GetAggregation<long>("count()")));
                }

                Assert.True(pages <= groupCount, "pagination did not terminate");
            }

            Assert.True(pages > 1, $"expected multiple pages, got {pages}");
            Assert.Equal(groupCount, seenGroups.Distinct().Count());
            Assert.Equal(seenGroups.Count, seenGroups.Distinct().Count()); // no duplicates across pages
        }
        finally
        {
            foreach (var id in ids)
                await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task GroupByAsync_SummaryPerGroup_ReturnsHitsInsideGroups()
    {
        if (!Enabled) return;

        var runTag = $"sum-{Guid.NewGuid():N}";
        var ids = new List<string>();
        try
        {
            var docs = new[]
            {
                new TestProduct { Name = "Sum A1", Price = 1, Category = $"{runTag}-a", Tag = runTag },
                new TestProduct { Name = "Sum A2", Price = 2, Category = $"{runTag}-a", Tag = runTag },
                new TestProduct { Name = "Sum A3", Price = 3, Category = $"{runTag}-a", Tag = runTag },
                new TestProduct { Name = "Sum B1", Price = 4, Category = $"{runTag}-b", Tag = runTag },
                new TestProduct { Name = "Sum B2", Price = 5, Category = $"{runTag}-b", Tag = runTag },
            };
            foreach (var doc in docs)
            {
                var id = NewId();
                ids.Add(id);
                await fixture.Client.Documents.PutAsync(id, doc);
            }
            await Task.Delay(3000);

            var request = YqlBuilder.Select().From("product")
                .Where(w => w.Field("tag").Contains(runTag))
                .GroupBy(GroupingBuilder.All()
                    .Group("category")
                    .Each(e => e.Output(GroupingAgg.Count()).Summary(maxHits: 2)))
                .ToSearchRequest();

            var result = await fixture.Client.Search.GroupByAsync<TestProduct>(request);

            Assert.NotEmpty(result.GroupingResults);
            var groups = result.GroupingResults[0].Groups;
            Assert.Equal(2, groups.Count);

            foreach (var group in groups)
            {
                Assert.InRange(group.Hits.Count, 1, 2);
                foreach (var hitObj in group.Hits)
                {
                    var hit = Assert.IsType<SearchHit<TestProduct>>(hitObj);
                    Assert.NotNull(hit.Fields);
                    Assert.StartsWith("Sum ", hit.Fields!.Name);
                }
            }
        }
        finally
        {
            foreach (var id in ids)
                await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }
}
