using System.Diagnostics;
using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Query;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public sealed class ApiConformanceE2ETests(VespaFixture fixture)
{
    [Fact]
    public async Task InvalidYql_PreservesServerErrorEnvelope()
    {
        if (!VespaFixture.IntegrationEnabled) return;

        var error = await Assert.ThrowsAsync<VespaException>(() =>
            fixture.Client.Search.QueryAsync<TestProduct>("select bad syntax"));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal(4, error.Error!.Code);
        Assert.Equal("Invalid query parameter", error.Error.Summary);
        Assert.Contains("YQL", error.Message);
    }

    [Fact]
    public async Task Grouping_PreservesGlobalCountAndRestBucket()
    {
        if (!VespaFixture.IntegrationEnabled) return;

        var category = $"conformance-{Guid.NewGuid():N}";
        var ids = Enumerable.Range(0, 3).Select(i => $"{category}-{i}").ToArray();
        try
        {
            for (var i = 0; i < ids.Length; i++)
                await fixture.Client.Documents.PutAsync(ids[i], new TestProduct
                {
                    Name = $"Conformance {i}",
                    Category = category,
                    Price = i + 0.5
                });

            var yql = YqlBuilder<TestProduct>.Select().Where(w => w.Field(p => p.Category).Contains(category)).Build();
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                var search = await fixture.Client.Search.QueryAsync<TestProduct>(yql);
                if (search.Root.Fields?.TotalCount == ids.Length)
                    break;
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "Seed documents did not become searchable.");
                await Task.Delay(100);
            }

            var global = await fixture.Client.Search.GroupByAsync<TestProduct>(new()
            {
                Yql = $"{yql} | all(output(count()))",
                Hits = 0,
                ModelRestrict = "product"
            });
            Assert.Equal(3, Assert.Single(global.GroupingRoots).Aggregations["count()"]);
            Assert.True(global.Coverage!.Full);
            Assert.True(global.Errors is null or { Count: 0 });

            var buckets = await fixture.Client.Search.GroupByAsync<TestProduct>(new()
            {
                // Integer bounds would round the double price before comparison.
                Yql = $"{yql} | all(group(predefined(price, bucket[0.0,1.0))) each(output(count())))",
                Hits = 0,
                ModelRestrict = "product"
            });
            var groups = Assert.Single(buckets.GroupingResults).Groups;
            Assert.Equal(2, Assert.Single(groups, group => group.IsRestBucket).Aggregations["count()"]);
            Assert.Equal(3, groups.Sum(group => group.Aggregations["count()"]));
        }
        finally
        {
            await fixture.Client.Feed.BulkDeleteAsync(ids, "product", @namespace: "test");
        }
    }

    [Fact]
    public async Task Jsonl_GroupVisit_PreservesIdentityForSubsequentDocumentOperations()
    {
        if (!VespaFixture.IntegrationEnabled) return;

        var group = $"conformance-{Guid.NewGuid():N}";
        const string localId = "part:one";
        try
        {
            await fixture.Client.Documents.PutByGroupAsync(group, localId,
                new TestEvent { EventName = "Identity", Payload = "payload" }, "event", @namespace: "test");

            var documents = new List<VespaDocument<TestEvent>>();
            await foreach (var document in fixture.Client.Documents.VisitJsonlAsync<TestEvent>(
                "event", cluster: "content", @namespace: "test", options: new VisitOptions { Group = group }))
                documents.Add(document);

            var visited = Assert.Single(documents);
            Assert.Equal($"id:test:event:g={group}:{localId}", visited.FullId);
            Assert.Equal(localId, visited.Id);
            var fetched = await fixture.Client.Documents.GetAsync<TestEvent>(visited.FullId, "event", @namespace: "test");
            Assert.Equal("Identity", fetched!.Fields!.EventName);
        }
        finally
        {
            await fixture.Client.Documents.DeleteByGroupAsync(group, localId, "event", @namespace: "test");
        }
    }
}
