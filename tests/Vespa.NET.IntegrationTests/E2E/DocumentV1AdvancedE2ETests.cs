using Vespa.Documents;
using Vespa.Feed;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Models.Tensors;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

/// <summary>
/// E2E coverage for the newer document/v1 paths: conditional put with create,
/// group/number-level visiting (against the streaming-mode event type), tensor
/// field-update operations (modify/add/remove) and update-where spanning multiple
/// server chunks (continuation handling).
/// </summary>
[Collection("Vespa")]
[Trait("Category", "E2E")]
public class DocumentV1AdvancedE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    private static string NewId() => $"docv1-{Guid.NewGuid():N}";

    // ── Conditional put with create ──────────────────────────────────────────

    [Fact]
    public async Task ConditionalPut_WithCreate_OnMissingDocument_CreatesDocument()
    {
        if (!Enabled) return;

        var id = NewId();
        try
        {
            // Document does not exist; condition + create=true must apply the put.
            var response = await fixture.Client.Documents.PutAsync(
                id,
                new TestProduct { Name = "created", Price = 5, Category = "cond-create" },
                condition: "product.category==\"cond-create\"",
                create: true);
            Assert.True(response.IsSuccess);

            var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
            Assert.Equal("created", doc?.Fields?.Name);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task ConditionalPut_WithoutCreate_OnMissingDocument_ThrowsConditionNotMet()
    {
        if (!Enabled) return;

        var id = NewId();
        await Assert.ThrowsAsync<VespaConditionNotMetException>(() =>
            fixture.Client.Documents.PutAsync(
                id,
                new TestProduct { Name = "never", Price = 1, Category = "cond-nocreate" },
                condition: "product.category==\"cond-nocreate\""));
    }

    // ── Group / number-level visiting ────────────────────────────────────────

    [Fact]
    public async Task VisitAsync_GroupLevel_ReturnsOnlyGroupDocuments()
    {
        if (!Enabled) return;

        var groupA = $"va-{Guid.NewGuid():N}";
        var groupB = $"vb-{Guid.NewGuid():N}";
        var lids = new List<(string Group, string Lid)>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var lid = NewId();
                lids.Add((groupA, lid));
                await fixture.Client.Documents.PutByGroupAsync(
                    groupA, lid, new TestEvent { EventName = $"a-{i}", Payload = groupA },
                    "event", @namespace: "test");
            }
            for (var i = 0; i < 2; i++)
            {
                var lid = NewId();
                lids.Add((groupB, lid));
                await fixture.Client.Documents.PutByGroupAsync(
                    groupB, lid, new TestEvent { EventName = $"b-{i}", Payload = groupB },
                    "event", @namespace: "test");
            }
            await Task.Delay(2000);

            var visited = new List<VespaDocument<TestEvent>>();
            await foreach (var doc in fixture.Client.Documents.VisitAsync<TestEvent>(
                "event", cluster: "content", @namespace: "test",
                options: new VisitOptions { Group = groupA }))
                visited.Add(doc);

            Assert.Equal(3, visited.Count);
            Assert.All(visited, d => Assert.Equal(groupA, d.Fields?.Payload));
        }
        finally
        {
            foreach (var (group, lid) in lids)
                await fixture.Client.Documents.DeleteByGroupAsync(group, lid, "event", @namespace: "test");
        }
    }

    [Fact]
    public async Task VisitAsync_NumberLevel_ReturnsOnlyBucketDocuments()
    {
        if (!Enabled) return;

        var bucketA = Random.Shared.NextInt64(1, long.MaxValue);
        var bucketB = Random.Shared.NextInt64(1, long.MaxValue);
        var lids = new List<(long Bucket, string Lid)>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var lid = NewId();
                lids.Add((bucketA, lid));
                await fixture.Client.Documents.PutByNumberAsync(
                    bucketA, lid, new TestEvent { EventName = $"na-{i}", Payload = "bucket-a" },
                    "event", @namespace: "test");
            }
            var otherLid = NewId();
            lids.Add((bucketB, otherLid));
            await fixture.Client.Documents.PutByNumberAsync(
                bucketB, otherLid, new TestEvent { EventName = "nb-0", Payload = "bucket-b" },
                "event", @namespace: "test");
            await Task.Delay(2000);

            var visited = new List<VespaDocument<TestEvent>>();
            await foreach (var doc in fixture.Client.Documents.VisitAsync<TestEvent>(
                "event", cluster: "content", @namespace: "test",
                options: new VisitOptions { Number = bucketA }))
                visited.Add(doc);

            Assert.Equal(2, visited.Count);
            Assert.All(visited, d => Assert.Equal("bucket-a", d.Fields?.Payload));
        }
        finally
        {
            foreach (var (bucket, lid) in lids)
                await fixture.Client.Documents.DeleteByNumberAsync(bucket, lid, "event", @namespace: "test");
        }
    }

    // ── Tensor field-update operations ───────────────────────────────────────

    private async Task<string> SeedWeightsAsync(Dictionary<string, float> weights)
    {
        var id = NewId();
        await fixture.Client.Documents.PutAsync(id, new TestProduct
        {
            Name = "tensor-update",
            Category = "tensor-upd",
            Weights = VespaTensor.FromMappedValues(weights)
        });
        return id;
    }

    private async Task<Dictionary<string, float>?> GetWeightsAsync(string id)
    {
        var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
        return doc?.Fields?.Weights?.GetMappedValues<float>();
    }

    [Fact]
    public async Task TensorModify_ReplaceCells_UpdatesMappedTensor()
    {
        if (!Enabled) return;

        var id = await SeedWeightsAsync(new() { ["a"] = 1f, ["b"] = 2f });
        try
        {
            var response = await fixture.Client.Documents.UpdateFieldsAsync<TestProduct>(id,
                new Dictionary<string, FieldOperation>
                {
                    ["weights"] = FieldOp.ModifyCells("replace", new Dictionary<string, double> { ["a"] = 5.0 })
                });
            Assert.True(response.IsSuccess);

            var weights = await GetWeightsAsync(id);
            Assert.NotNull(weights);
            Assert.Equal(5f, weights["a"]);
            Assert.Equal(2f, weights["b"]); // untouched cell survives
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task TensorAdd_AddsCellsToMappedTensor()
    {
        if (!Enabled) return;

        var id = await SeedWeightsAsync(new() { ["a"] = 1f });
        try
        {
            var response = await fixture.Client.Documents.UpdateFieldsAsync<TestProduct>(id,
                new Dictionary<string, FieldOperation>
                {
                    ["weights"] = FieldOp.TensorAdd(
                    [
                        new TensorCellUpdate(new() { ["cat"] = "b" }, 3.5),
                        new TensorCellUpdate(new() { ["cat"] = "a" }, 9.0) // overwrites existing address
                    ])
                });
            Assert.True(response.IsSuccess);

            var weights = await GetWeightsAsync(id);
            Assert.NotNull(weights);
            Assert.Equal(2, weights.Count);
            Assert.Equal(9f, weights["a"]);
            Assert.Equal(3.5f, weights["b"]);
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Fact]
    public async Task TensorRemove_RemovesCellsFromMappedTensor()
    {
        if (!Enabled) return;

        var id = await SeedWeightsAsync(new() { ["a"] = 1f, ["b"] = 2f, ["c"] = 3f });
        try
        {
            var response = await fixture.Client.Documents.UpdateFieldsAsync<TestProduct>(id,
                new Dictionary<string, FieldOperation>
                {
                    ["weights"] = FieldOp.TensorRemove([new Dictionary<string, string> { ["cat"] = "b" }])
                });
            Assert.True(response.IsSuccess);

            var weights = await GetWeightsAsync(id);
            Assert.NotNull(weights);
            Assert.Equal(2, weights.Count);
            Assert.Equal(1f, weights["a"]);
            Assert.Equal(3f, weights["c"]);
            Assert.False(weights.ContainsKey("b"));
        }
        finally
        {
            await fixture.Client.Documents.DeleteAsync<TestProduct>(id);
        }
    }

    // ── Update-where across multiple chunks ──────────────────────────────────
    // Probing a real Vespa showed: timeChunk=1ms livelocks (the server keeps
    // returning the same continuation token without progressing), and documentCount
    // is at-least-once across chunk boundaries (a boundary document may be counted
    // in two chunks). 20ms over 120 documents reliably yields several chunks and
    // terminates, so assertions use >= for the count plus exact-value verification.

    private async Task<List<string>> SeedChunkDocsAsync(string category, int count)
    {
        var docs = Enumerable.Range(0, count).Select(i => new FeedDocument<TestProduct>
        {
            Id = NewId(),
            Fields = new TestProduct { Name = $"Chunk {i}", Price = 1, Category = category }
        }).ToList();

        await fixture.Client.Feed.BulkPutAsync(docs, "product", @namespace: "test");
        await Task.Delay(2000);
        return [.. docs.Select(d => d.Id)];
    }

    [Fact]
    public async Task UpdateBySelection_SmallTimeChunk_UpdatesAllDocumentsAcrossChunks()
    {
        if (!Enabled) return;

        const int documentCount = 120;
        var category = $"chunked-{Guid.NewGuid():N}";
        var ids = await SeedChunkDocsAsync(category, documentCount);
        try
        {
            // A small timeChunk makes Vespa return continuation tokens — exercising the
            // client's internal loop, which must sum documentCount across all chunks.
            var result = await fixture.Client.Documents.UpdateBySelectionAsync(
                $"product.category == \"{category}\"",
                new Dictionary<string, FieldOperation> { ["price"] = FieldOp.Assign(77.0) },
                "product", @namespace: "test", cluster: "content",
                requestOptions: new SelectionRequestOptions { TimeChunk = TimeSpan.FromMilliseconds(20) });

            Assert.True(result.IsSuccess);
            Assert.True(result.DocumentCount >= documentCount,
                $"expected documentCount >= {documentCount}, got {result.DocumentCount}");
            await Task.Delay(2000);

            foreach (var id in ids)
            {
                var doc = await fixture.Client.Documents.GetAsync<TestProduct>(id);
                Assert.Equal(77.0, doc?.Fields?.Price);
            }
        }
        finally
        {
            await fixture.Client.Feed.BulkDeleteAsync(ids, "product", @namespace: "test");
        }
    }

    [Fact]
    public async Task UpdateBySelectionPage_SmallTimeChunk_ReturnsContinuationAtLeastOnce()
    {
        if (!Enabled) return;

        const int documentCount = 120;
        var category = $"chunked-page-{Guid.NewGuid():N}";
        var ids = await SeedChunkDocsAsync(category, documentCount);
        try
        {
            var options = new SelectionRequestOptions { TimeChunk = TimeSpan.FromMilliseconds(20) };
            long total = 0;
            var pages = 0;
            string? continuation = null;
            do
            {
                var page = await fixture.Client.Documents.UpdateBySelectionPageAsync(
                    $"product.category == \"{category}\"",
                    new Dictionary<string, FieldOperation> { ["price"] = FieldOp.Assign(66.0) },
                    "product", @namespace: "test", cluster: "content",
                    continuation: continuation, requestOptions: options);
                total += page.DocumentCount;
                continuation = page.Continuation;
                pages++;
                if (pages > 2000) throw new InvalidOperationException("pagination did not terminate");
            } while (continuation is not null);

            Assert.True(pages > 1, $"expected more than one chunk, got {pages}");
            Assert.True(total >= documentCount, $"expected total >= {documentCount}, got {total}");
        }
        finally
        {
            await fixture.Client.Feed.BulkDeleteAsync(ids, "product", @namespace: "test");
        }
    }
}
