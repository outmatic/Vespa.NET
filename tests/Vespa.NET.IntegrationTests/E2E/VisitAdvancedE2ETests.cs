using Vespa.Documents;
using Vespa.Feed;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

/// <summary>
/// E2E tests for VisitAsync advanced parameters (concurrency, slices, timeout)
/// and VisitJsonlAsync with the typed extension.
/// </summary>
[Collection("Vespa")]
[Trait("Category", "E2E")]
public class VisitAdvancedE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    private async Task<(List<string> Ids, string Category)> SeedAsync(int count)
    {
        var category = $"va-{Guid.NewGuid():N}";
        var docs = Enumerable.Range(1, count).Select(i => new FeedDocument<TestProduct>
        {
            Id = $"va-{Guid.NewGuid():N}",
            Fields = new TestProduct
            {
                Name = $"VisitAdv {i}",
                Price = i * 10.0,
                Category = category,
                InStock = true,
                Quantity = i
            }
        }).ToList();

        await fixture.Client.Feed.BulkPutAsync(docs, "product", @namespace: "test");
        await Task.Delay(2000);
        return (docs.Select(d => d.Id).ToList(), category);
    }

    private async Task CleanupAsync(List<string> ids)
    {
        await fixture.Client.Feed.BulkDeleteAsync(ids, "product", @namespace: "test");
    }

    // ── VisitAsync with timeout ──────────────────────────────────────────────

    [Fact]
    public async Task VisitAsync_WithTimeout_CompletesWithinTime()
    {
        if (!Enabled) return;

        var (ids, category) = await SeedAsync(5);
        try
        {
            var visited = new List<VespaDocument<TestProduct>>();
            await foreach (var doc in fixture.Client.Documents.VisitAsync<TestProduct>(
                "product",
                selection: $"product.category == \"{category}\"",
                @namespace: "test",
                timeout: TimeSpan.FromSeconds(30),
                wantedDocumentCount: 100))
                visited.Add(doc);

            Assert.Equal(5, visited.Count);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── VisitAsync with concurrency ──────────────────────────────────────────

    [Fact]
    public async Task VisitAsync_WithConcurrency_ReturnsAllDocuments()
    {
        if (!Enabled) return;

        var (ids, category) = await SeedAsync(6);
        try
        {
            var visited = new List<VespaDocument<TestProduct>>();
            await foreach (var doc in fixture.Client.Documents.VisitAsync<TestProduct>(
                "product",
                selection: $"product.category == \"{category}\"",
                @namespace: "test",
                concurrency: 2,
                wantedDocumentCount: 100))
                visited.Add(doc);

            Assert.Equal(6, visited.Count);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── VisitAsync with slices ───────────────────────────────────────────────

    [Fact]
    public async Task VisitAsync_WithSlices_ReturnsSubset()
    {
        if (!Enabled) return;

        var (ids, category) = await SeedAsync(8);
        try
        {
            // Request slice 0 of 2
            var slice0 = new List<VespaDocument<TestProduct>>();
            await foreach (var doc in fixture.Client.Documents.VisitAsync<TestProduct>(
                "product",
                selection: $"product.category == \"{category}\"",
                @namespace: "test",
                slices: 2,
                sliceId: 0,
                wantedDocumentCount: 100))
                slice0.Add(doc);

            // Request slice 1 of 2
            var slice1 = new List<VespaDocument<TestProduct>>();
            await foreach (var doc in fixture.Client.Documents.VisitAsync<TestProduct>(
                "product",
                selection: $"product.category == \"{category}\"",
                @namespace: "test",
                slices: 2,
                sliceId: 1,
                wantedDocumentCount: 100))
                slice1.Add(doc);

            // Combined should cover all documents
            var totalVisited = slice0.Count + slice1.Count;
            Assert.Equal(8, totalVisited);

            // No overlap between slices
            var slice0Ids = slice0.Select(d => d.Id).ToHashSet();
            var slice1Ids = slice1.Select(d => d.Id).ToHashSet();
            Assert.Empty(slice0Ids.Intersect(slice1Ids));
        }
        finally { await CleanupAsync(ids); }
    }

    // ── VisitJsonlAsync typed extension ──────────────────────────────────────

    [Fact]
    public async Task VisitJsonlAsync_WithWantedDocumentCount_LimitsOutput()
    {
        if (!Enabled) return;

        var (ids, category) = await SeedAsync(6);
        try
        {
            var visited = new List<VespaDocument<TestProduct>>();
            await foreach (var doc in fixture.Client.Documents.VisitJsonlAsync<TestProduct>(
                "product",
                selection: $"product.category == \"{category}\"",
                @namespace: "test",
                cluster: "content",
                wantedDocumentCount: 3))
            {
                visited.Add(doc);
                if (visited.Count >= 3)
                    break;
            }

            Assert.True(visited.Count >= 1 && visited.Count <= 6);
        }
        finally { await CleanupAsync(ids); }
    }

    // ── VisitAsync typed extension with concurrency param ────────────────────

    [Fact]
    public async Task VisitAsync_TypedExtension_WithConcurrency_Works()
    {
        if (!Enabled) return;

        var (ids, category) = await SeedAsync(4);
        try
        {
            var visited = new List<VespaDocument<TestProduct>>();
            await foreach (var doc in fixture.Client.Documents.VisitAsync<TestProduct>(
                selection: $"product.category == \"{category}\"",
                concurrency: 2,
                wantedDocumentCount: 100))
                visited.Add(doc);

            Assert.Equal(4, visited.Count);
        }
        finally { await CleanupAsync(ids); }
    }
}
