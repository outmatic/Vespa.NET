using System.Runtime.CompilerServices;
using Vespa.Documents;
using Vespa.Feed;
using Vespa.IntegrationTests.Fixtures;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public class FeedAdvancedE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    // ── FeedAsync with cancellation ──────────────────────────────────────────

    [Fact]
    public async Task FeedAsync_CancellationToken_StopsEarly()
    {
        if (!Enabled) return;

        using var cts = new CancellationTokenSource();
        var processedIds = new List<string>();

        // Generate 50 docs but cancel after seeing a few.
        // FeedAsync may throw TaskCanceledException or OperationCanceledException — that's expected.
        FeedResult? result = null;
        try
        {
            result = await fixture.Client.Feed.FeedAsync(
                GenerateWithCancellation(50, cts, processedIds),
                "product",
                @namespace: "test",
                maxConcurrency: 2,
                boundedCapacity: 4,
                cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected — pipeline was cancelled
        }

        // Either we got a partial result or cancellation was immediate
        if (result is not null)
            Assert.True(result.TotalDocuments < 50,
                $"Expected fewer than 50 documents but got {result.TotalDocuments}");

        // We should have yielded some docs before cancellation
        Assert.True(processedIds.Count > 0, "Expected at least some documents to be yielded");
        Assert.True(processedIds.Count < 50, "Expected cancellation to stop before all 50 docs");

        // Cleanup whatever was fed
        await fixture.Client.Feed.BulkDeleteAsync(processedIds, "product", @namespace: "test");
    }

    private static async IAsyncEnumerable<FeedDocument<TestProduct>> GenerateWithCancellation(
        int count, CancellationTokenSource cts, List<string> processedIds,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < count; i++)
        {
            if (ct.IsCancellationRequested) yield break;

            var id = $"feed-cancel-{Guid.NewGuid():N}";
            processedIds.Add(id);

            yield return new FeedDocument<TestProduct>
            {
                Id = id,
                Fields = new TestProduct { Name = $"Cancel-{i}", Price = i, Category = "cancel-test" }
            };

            // Cancel after yielding 5 docs
            if (i == 4)
                cts.Cancel();
        }
    }

    // ── BulkPut with per-document conditions ─────────────────────────────────

    [Fact]
    public async Task BulkPut_WithCondition_AppliesPerDocument()
    {
        if (!Enabled) return;

        var existingId = $"feed-cond-{Guid.NewGuid():N}";
        var newId = $"feed-cond-{Guid.NewGuid():N}";

        // Create one existing document
        await fixture.Client.Documents.PutAsync(existingId, new TestProduct
        {
            Name = "Existing",
            Price = 50,
            Category = "cond-test"
        });
        await Task.Delay(500);

        // BulkPut: one doc with condition that matches, one with condition that doesn't
        var docs = new List<FeedDocument<TestProduct>>
        {
            new()
            {
                Id = existingId,
                Fields = new TestProduct { Name = "Updated", Price = 100, Category = "cond-test" },
                Condition = "product.price > 10" // will match (price=50)
            },
            new()
            {
                Id = newId,
                Fields = new TestProduct { Name = "New", Price = 25, Category = "cond-test" },
                // No condition — should succeed
            }
        };

        var result = await fixture.Client.Feed.BulkPutAsync(docs, "product", @namespace: "test");

        // Both should succeed (condition met for existing, no condition for new)
        Assert.Equal(2, result.SuccessCount);

        await Task.Delay(500);
        var updated = await fixture.Client.Documents.GetAsync<TestProduct>(existingId);
        Assert.Equal("Updated", updated?.Fields?.Name);

        // Cleanup
        await fixture.Client.Documents.DeleteAsync<TestProduct>(existingId);
        await fixture.Client.Documents.DeleteAsync<TestProduct>(newId);
    }

    [Fact]
    public async Task BulkPut_WithFailingCondition_ReportsErrors()
    {
        if (!Enabled) return;

        var existingId = $"feed-cfail-{Guid.NewGuid():N}";

        await fixture.Client.Documents.PutAsync(existingId, new TestProduct
        {
            Name = "Low",
            Price = 5,
            Category = "cond-fail"
        });
        await Task.Delay(500);

        var docs = new List<FeedDocument<TestProduct>>
        {
            new()
            {
                Id = existingId,
                Fields = new TestProduct { Name = "ShouldFail", Price = 999, Category = "cond-fail" },
                Condition = "product.price > 100" // won't match (price=5)
            }
        };

        var result = await fixture.Client.Feed.BulkPutAsync(docs, "product", @namespace: "test");

        // Condition not met → failure
        Assert.Equal(1, result.FailureCount);
        Assert.Equal(0, result.SuccessCount);

        // Document unchanged
        var doc = await fixture.Client.Documents.GetAsync<TestProduct>(existingId);
        Assert.Equal("Low", doc?.Fields?.Name);

        await fixture.Client.Documents.DeleteAsync<TestProduct>(existingId);
    }
}
