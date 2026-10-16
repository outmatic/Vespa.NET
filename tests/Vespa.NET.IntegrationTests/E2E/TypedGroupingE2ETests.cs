using System.Diagnostics;
using System.Text.Json.Serialization;
using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Query;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public sealed class TypedGroupingE2ETests(VespaFixture fixture)
{
    [Fact]
    public async Task AsTyped_PreservesNestedSummariesAndQuantiles()
    {
        if (!VespaFixture.IntegrationEnabled) return;

        var tag = $"typed-grouping-{Guid.NewGuid():N}";
        TestProduct[] products =
        [
            new() { Name = "Typed A1", Category = "a", Quantity = 1, Price = 1.25, Tag = tag },
            new() { Name = "Typed A2", Category = "a", Quantity = 2, Price = 2.75, Tag = tag },
            new() { Name = "Typed B1", Category = "b", Quantity = 3, Price = 10, Tag = tag }
        ];
        var ids = Enumerable.Range(0, products.Length).Select(i => $"{tag}-{i}").ToArray();
        try
        {
            for (var i = 0; i < products.Length; i++)
                await fixture.Client.Documents.PutAsync(ids[i], products[i]);

            var query = YqlBuilder<TestProduct>.Select().Where(w => w.Field(p => p.Tag).Contains(tag));
            var yql = query.Build();
            await WaitForDocumentsAsync<TestProduct>(yql, products.Length);

            var global = (await fixture.Client.Search.GroupByAsync<TestProduct>(new()
            {
                Yql = $"{yql} | all(output(count() as(total)))",
                Hits = 0
            })).AsTyped();
            var root = Assert.Single(global.GroupingRoots);
            Assert.Equal(products.Length, root.GetAggregation<long>("total"));
            Assert.True(global.Coverage!.Full);
            Assert.True(global.Errors is null or { Count: 0 });

            var request = query.GroupBy(GroupingBuilder.All().Group("category")
                .Each(each => each
                    .Output(
                        GroupingAgg.As(GroupingAgg.Count(), "total"),
                        GroupingAgg.As(GroupingAgg.Sum("price"), "amount"),
                        GroupingAgg.As(GroupingAgg.Quantiles([0.5], "price"), "percentiles"))
                    .Summary(maxHits: 2)
                    .SubGroup(GroupingBuilder.All().Group("quantity")
                        .Each(inner => inner.Output(GroupingAgg.Count()).Summary(maxHits: 1)))))
                .ToSearchRequest();
            request.Hits = 0;

            var result = (await fixture.Client.Search.GroupByAsync<TestProduct>(request)).AsTyped();
            var list = Assert.Single(result.GroupingResults);
            Assert.Equal(2, list.Groups.Count);
            Assert.Same(list, Assert.Single(Assert.Single(result.GroupingRoots).SubGroups));

            foreach (var group in list.Groups)
            {
                var expected = products.Where(product => product.Category == group.Value).ToArray();
                Assert.Equal(expected.Length, group.GetAggregation<long>("total"));
                Assert.Equal(expected.Sum(product => product.Price), group.GetAggregation<double>("amount"));
                Assert.Equal(expected.Length, group.Hits.Count);
                foreach (var hit in group.Hits)
                {
                    Assert.Equal(tag, hit.Fields!.Tag);
                    Assert.Contains(hit.Fields.Name, expected.Select(product => product.Name));
                    Assert.Contains(hit.Id, ids);
                }

                var quantile = Assert.Single(group.GetAggregation<Quantile[]>("percentiles")!);
                Assert.Equal(0.5, quantile.Position);
                Assert.InRange(quantile.Value, expected.Min(product => product.Price), expected.Max(product => product.Price));

                var nested = Assert.Single(group.SubGroups);
                Assert.Equal(expected.Length, nested.Groups.Count);
                foreach (var bucket in nested.Groups)
                {
                    Assert.Equal(1, bucket.GetAggregation<long>("count()"));
                    var hit = Assert.Single(bucket.Hits);
                    Assert.Equal(tag, hit.Fields!.Tag);
                    Assert.Equal(hit.Fields.Quantity.ToString(), bucket.Value);
                }
            }
        }
        finally
        {
            await fixture.Client.Feed.BulkDeleteAsync(ids, "product", @namespace: "test");
        }
    }

    [Fact]
    public async Task AsTyped_Int64Aggregations_PreserveLargeStoredValues()
    {
        if (!VespaFixture.IntegrationEnabled) return;

        var tag = $"typedlong{Guid.NewGuid():N}";
        string[] ids = [$"{tag}-1", $"{tag}-2"];
        try
        {
            await fixture.Client.Documents.PutAsync(ids[0], new BaseItem { Title = tag, BaseScore = 9007199254740993 });
            await fixture.Client.Documents.PutAsync(ids[1], new BaseItem { Title = tag, BaseScore = 2 });
            var yql = YqlBuilder<BaseItem>.Select().Where(w => w.Field(item => item.Title).Contains(tag)).Build();
            await WaitForDocumentsAsync<BaseItem>(yql, ids.Length);

            var result = (await fixture.Client.Search.GroupByAsync<BaseItem>(new()
            {
                Yql = $"{yql} | all(group(1) each(output(count() as(total), sum(base_score) as(amount), max(base_score) as(largest))))",
                Hits = 0,
                ModelRestrict = "base_item"
            })).AsTyped();

            var group = Assert.Single(Assert.Single(result.GroupingResults).Groups);
            Assert.Equal(2, group.GetAggregation<long>("total"));
            Assert.Equal(9007199254740995L, group.GetAggregation<long>("amount"));
            Assert.Equal(9007199254740993L, group.GetAggregation<long>("largest"));
            Assert.True(group.TryGetAggregation<long>("largest", out var largest));
            Assert.Equal(9007199254740993L, largest);
        }
        finally
        {
            await fixture.Client.Feed.BulkDeleteAsync(ids, "base_item", @namespace: "test");
        }
    }

    private async Task WaitForDocumentsAsync<T>(string yql, int count) where T : class
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var search = await fixture.Client.Search.QueryAsync<T>(yql);
            if (search.Root.Fields?.TotalCount == count)
                return;
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "Seed documents did not become searchable.");
            await Task.Delay(100);
        }
    }

    private sealed record Quantile(
        [property: JsonPropertyName("quantile")] double Position,
        [property: JsonPropertyName("value")] double Value);
}
