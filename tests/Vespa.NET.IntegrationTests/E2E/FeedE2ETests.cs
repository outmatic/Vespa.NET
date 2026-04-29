using System.Runtime.CompilerServices;
using Vespa.Documents;
using Vespa.Feed;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Query;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public class FeedE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    // ── BulkPut ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkPut_AllDocuments_Succeed()
    {
        if (!Enabled) return;

        var docs = Enumerable.Range(1, 10).Select(i => new FeedDocument<TestProduct>
        {
            Id = $"feed-bulk-{Guid.NewGuid():N}",
            Fields = new TestProduct { Name = $"Product {i}", Price = i * 10.0, Category = "bulk-test" }
        }).ToList();

        var result = await fixture.Client.Feed.BulkPutAsync(docs, "product", @namespace: "test");

        Assert.True(result.IsSuccess);
        Assert.Equal(docs.Count, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);

        await fixture.Client.Feed.BulkDeleteAsync(docs.Select(d => d.Id), "product", @namespace: "test");
    }

    [Fact]
    public async Task BulkPut_LargerBatch_RespectsMaxConcurrency()
    {
        if (!Enabled) return;

        var docs = Enumerable.Range(1, 25).Select(i => new FeedDocument<TestProduct>
        {
            Id = $"feed-conc-{Guid.NewGuid():N}",
            Fields = new TestProduct { Name = $"Concurrent {i}", Price = i, Category = "concurrency-test" }
        }).ToList();

        var result = await fixture.Client.Feed.BulkPutAsync(
            docs, "product", @namespace: "test", maxConcurrency: 5);

        Assert.True(result.IsSuccess);
        Assert.Equal(25, result.SuccessCount);

        await fixture.Client.Feed.BulkDeleteAsync(docs.Select(d => d.Id), "product", @namespace: "test");
    }

    // ── BulkDelete ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkDelete_RemovesAllDocuments()
    {
        if (!Enabled) return;

        var ids = Enumerable.Range(1, 5).Select(_ => $"feed-del-{Guid.NewGuid():N}").ToList();

        var docs = ids.Select((id, i) => new FeedDocument<TestProduct>
        {
            Id = id,
            Fields = new TestProduct { Name = $"ToDelete {i}", Price = i, Category = "del-test" }
        });
        await fixture.Client.Feed.BulkPutAsync(docs, "product", @namespace: "test");
        await Task.Delay(1500);

        var result = await fixture.Client.Feed.BulkDeleteAsync(ids, "product", @namespace: "test");

        Assert.True(result.IsSuccess);
        Assert.Equal(ids.Count, result.SuccessCount);
    }

    // ── BulkUpdate ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkUpdate_AppliesFieldOperationsPerDocument()
    {
        if (!Enabled) return;

        var ids = Enumerable.Range(1, 4).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        foreach (var (id, i) in ids.Select((v, i) => (v, i)))
        {
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = $"BU-{i}",
                Price = 10,
                Category = "bulk-upd"
            });
        }
        await Task.Delay(1500);

        var updates = ids.Select(id => new BulkFieldUpdate
        {
            Id = id,
            FieldOperations = new() { ["price"] = FieldOp.Assign(77.0) }
        });

        var result = await fixture.Client.Feed.BulkUpdateAsync(updates, "product", @namespace: "test");

        Assert.True(result.IsSuccess);
        Assert.Equal(ids.Length, result.SuccessCount);

        await Task.Delay(1500);
        foreach (var id in ids)
        {
            var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
            Assert.Equal(77.0, doc?.Fields?.Price);
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    // ── FeedAsync streaming pipeline ────────────────────────────────────────────

    [Fact]
    public async Task FeedAsync_StreamingPipeline_WithProgress()
    {
        if (!Enabled) return;

        var ids = Enumerable.Range(1, 15).Select(_ => $"feed-stream-{Guid.NewGuid():N}").ToList();
        var progressReports = new List<FeedProgress>();

        var result = await fixture.Client.Feed.FeedAsync(
            GenerateDocumentsAsync(ids),
            "product",
            @namespace: "test",
            maxConcurrency: 4,
            boundedCapacity: 8,
            onProgress: p => progressReports.Add(p));

        Assert.True(result.IsSuccess);
        Assert.Equal(ids.Count, result.SuccessCount);
        Assert.NotEmpty(progressReports);
        Assert.True(result.Duration > TimeSpan.Zero);

        await fixture.Client.Feed.BulkDeleteAsync(ids, "product", @namespace: "test");
    }

    private static async IAsyncEnumerable<FeedDocument<TestProduct>> GenerateDocumentsAsync(
        List<string> ids, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) yield break;
            yield return new FeedDocument<TestProduct>
            {
                Id = id,
                Fields = new TestProduct { Name = $"Stream-{id[^6..]}", Price = 42.0, Category = "stream-test" }
            };
        }
    }

    // ── Feed result metrics ─────────────────────────────────────────────────────

    [Fact]
    public async Task FeedResult_HasCorrectMetrics()
    {
        if (!Enabled) return;

        var docs = Enumerable.Range(1, 3).Select(i => new FeedDocument<TestProduct>
        {
            Id = $"feed-metrics-{Guid.NewGuid():N}",
            Fields = new TestProduct { Name = $"M {i}", Price = i, Category = "metrics-test" }
        }).ToList();

        var result = await fixture.Client.Feed.BulkPutAsync(docs, "product", @namespace: "test");

        Assert.Equal(3, result.TotalDocuments);
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.Equal(1.0, result.SuccessRate);
        Assert.True(result.Duration > TimeSpan.Zero);
        Assert.Empty(result.Errors);

        await fixture.Client.Feed.BulkDeleteAsync(docs.Select(d => d.Id), "product", @namespace: "test");
    }

    // ── BulkPut then search ─────────────────────────────────────────────────────

    [Fact]
    public async Task BulkPut_ThenSearch_DocumentsAreIndexed()
    {
        if (!Enabled) return;

        var category = $"feed-search-{Guid.NewGuid():N}";
        var docs = Enumerable.Range(1, 3).Select(i => new FeedDocument<TestProduct>
        {
            Id = $"feed-idx-{Guid.NewGuid():N}",
            Fields = new TestProduct { Name = $"Indexed {i}", Price = i * 5.0, Category = category }
        }).ToList();

        await fixture.Client.Feed.BulkPutAsync(docs, "product", @namespace: "test");
        await Task.Delay(2500);

        var yql = YqlBuilder<TestProduct>
            .Select()
            .Where(w => w.Field(p => p.Category).Contains(category))
            .Build();

        var result = await fixture.Client.Search.QueryAsync<TestProduct>(yql, hits: 10);
        Assert.Equal(3, result.Root.Children.Count);

        await fixture.Client.Feed.BulkDeleteAsync(docs.Select(d => d.Id), "product", @namespace: "test");
    }
}
