using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public class CrudAdvancedE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    private static string NewId() => $"crud-adv-{Guid.NewGuid():N}";

    // ── Combined field operations ────────────────────────────────────────────

    [Fact]
    public async Task UpdateFields_CombinedOperations_InSingleCall()
    {
        if (!Enabled) return;

        var id = NewId();
        await fixture.Client.Documents.PutAsync(id, new TestProduct
        {
            Name = "Combined",
            Price = 100,
            Category = "test",
            Quantity = 10
        });
        await Task.Delay(500);

        // Assign name + increment price + decrement quantity in one call
        await fixture.Client.Documents.UpdateFieldsAsync<TestProduct>(id,
            new Dictionary<string, FieldOperation>
            {
                ["product_name"] = FieldOp.Assign("CombinedUpdated"),
                ["price"] = FieldOp.Increment(50),
                ["quantity"] = FieldOp.Decrement(3)
            });
        await Task.Delay(300);

        var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
        Assert.Equal("CombinedUpdated", doc?.Fields?.Name);
        Assert.Equal(150.0, doc?.Fields?.Price);
        Assert.Equal(7, doc?.Fields?.Quantity);

        await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    // ── DeleteBySelectionAsync ────────────────────────────────────────────────

    [Fact]
    public async Task DeleteBySelection_RemovesMatchingDocuments()
    {
        if (!Enabled) return;

        var category = $"dbs-{Guid.NewGuid():N}";
        var ids = new List<string>();

        for (int i = 0; i < 5; i++)
        {
            var id = NewId();
            ids.Add(id);
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = $"DBS-{i}",
                Price = i * 10,
                Category = category
            });
        }
        await Task.Delay(2000);

        // Delete all docs in this category (cluster required for selection operations)
        var result = await fixture.Client.Documents.DeleteBySelectionAsync(
            $"product.category == \"{category}\"",
            "product", @namespace: "test", cluster: "content");

        Assert.True(result.IsSuccess);
        await Task.Delay(2000);

        // Verify they're gone
        foreach (var id in ids)
        {
            var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
            Assert.Null(doc);
        }
    }

    // ── UpdateBySelectionAsync ────────────────────────────────────────────────

    [Fact]
    public async Task UpdateBySelection_UpdatesMatchingDocuments()
    {
        if (!Enabled) return;

        var category = $"ubs-{Guid.NewGuid():N}";
        var ids = new List<string>();

        for (int i = 0; i < 3; i++)
        {
            var id = NewId();
            ids.Add(id);
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = $"UBS-{i}",
                Price = 10,
                Category = category
            });
        }
        await Task.Delay(2000);

        // Set price=99 for all docs in this category (cluster required for selection operations)
        var result = await fixture.Client.Documents.UpdateBySelectionAsync(
            $"product.category == \"{category}\"",
            new Dictionary<string, FieldOperation>
            {
                ["price"] = FieldOp.Assign(99.0)
            },
            "product", @namespace: "test", cluster: "content");

        Assert.True(result.IsSuccess);
        await Task.Delay(2000);

        // Verify updates
        foreach (var id in ids)
        {
            var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
            Assert.Equal(99.0, doc?.Fields?.Price);
        }

        // Cleanup
        foreach (var id in ids)
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    // ── VisitJsonlAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task VisitJsonlAsync_StreamsDocuments()
    {
        if (!Enabled) return;

        var category = $"jsonl-{Guid.NewGuid():N}";
        var ids = new List<string>();

        for (int i = 0; i < 4; i++)
        {
            var id = NewId();
            ids.Add(id);
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = $"Jsonl-{i}",
                Price = i * 5,
                Category = category
            });
        }
        await Task.Delay(2000);

        var visited = new List<VespaDocument<TestProduct>>();
        await foreach (var doc in fixture.Client.Documents.VisitJsonlAsync<TestProduct>(
            "product",
            selection: $"product.category == \"{category}\"",
            @namespace: "test",
            cluster: "content"))
            visited.Add(doc);

        Assert.Equal(4, visited.Count);

        // Cleanup
        foreach (var id in ids)
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }

    // ── Selection-op streaming (stream=true) ─────────────────────────────────

    [Fact]
    public async Task UpdateBySelection_WithStream_AppliesToAllMatchingDocuments()
    {
        if (!Enabled) return;

        var category = $"stream-upd-{Guid.NewGuid():N}";
        var ids = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            var id = NewId();
            ids.Add(id);
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = $"STR-{i}",
                Price = 1,
                Category = category
            });
        }
        await Task.Delay(2000);

        var result = await fixture.Client.Documents.UpdateBySelectionAsync(
            $"product.category == \"{category}\"",
            new Dictionary<string, FieldOperation> { ["price"] = FieldOp.Assign(55.0) },
            "product", @namespace: "test", cluster: "content",
            requestOptions: new SelectionRequestOptions { Stream = true });

        Assert.True(result.IsSuccess);
        await Task.Delay(2000);

        foreach (var id in ids)
        {
            var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
            Assert.Equal(55.0, doc?.Fields?.Price);
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    // ── Selection-op manual pagination ───────────────────────────────────────

    [Fact]
    public async Task DeleteBySelectionPageAsync_SingleChunk_CompletesOnSmallDataset()
    {
        if (!Enabled) return;

        var category = $"del-page-{Guid.NewGuid():N}";
        for (int i = 0; i < 3; i++)
        {
            await fixture.Client.Documents.PutAsync(NewId(), new TestProduct
            {
                Name = $"DP-{i}",
                Price = i,
                Category = category
            });
        }
        await Task.Delay(2000);

        // Small dataset: Vespa returns no continuation → IsComplete should be true after one call.
        var page = await fixture.Client.Documents.DeleteBySelectionPageAsync(
            $"product.category == \"{category}\"",
            "product", @namespace: "test", cluster: "content");

        Assert.True(page.IsComplete);
        Assert.Null(page.Continuation);
    }

    [Fact]
    public async Task UpdateBySelectionPageAsync_ManualLoopAcrossChunks_ProcessesAll()
    {
        if (!Enabled) return;

        var category = $"upd-page-{Guid.NewGuid():N}";
        var ids = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var id = NewId();
            ids.Add(id);
            await fixture.Client.Documents.PutAsync(id, new TestProduct
            {
                Name = $"UP-{i}",
                Price = 10,
                Category = category
            });
        }
        await Task.Delay(2000);

        // Drive the manual loop. Vespa may or may not return a continuation for a 3-doc set,
        // but the loop is the same in either case.
        long total = 0;
        string? continuation = null;
        int iterations = 0;
        do
        {
            var page = await fixture.Client.Documents.UpdateBySelectionPageAsync(
                $"product.category == \"{category}\"",
                new Dictionary<string, FieldOperation> { ["price"] = FieldOp.Assign(88.0) },
                "product", @namespace: "test", cluster: "content",
                continuation: continuation);
            total += page.DocumentCount;
            continuation = page.Continuation;
            iterations++;
            if (iterations > 20) throw new InvalidOperationException("pagination didn't terminate");
        } while (continuation is not null);

        await Task.Delay(2000);

        foreach (var id in ids)
        {
            var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
            Assert.Equal(88.0, doc?.Fields?.Price);
        }

        // Cleanup
        foreach (var id in ids)
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
    }
}
