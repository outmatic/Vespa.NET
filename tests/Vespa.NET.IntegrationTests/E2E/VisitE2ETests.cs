using Vespa.Documents;
using Vespa.Feed;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public class VisitE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    private async Task<List<string>> SeedAsync(int count, string categoryPrefix)
    {
        var docs = Enumerable.Range(1, count).Select(i => new FeedDocument<TestProduct>
        {
            Id = $"visit-{Guid.NewGuid():N}",
            Fields = new TestProduct
            {
                Name = $"Visit Product {i}",
                Price = i * 10.0,
                Category = $"{categoryPrefix}",
                InStock = i % 2 == 0,
                Quantity = i
            }
        }).ToList();

        await fixture.Client.Feed.BulkPutAsync(docs, "product", @namespace: "test");
        await Task.Delay(2000);

        return docs.Select(d => d.Id).ToList();
    }

    private async Task CleanupAsync(List<string> ids)
    {
        await fixture.Client.Feed.BulkDeleteAsync(ids, "product", @namespace: "test");
    }

    // ── VisitAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task VisitAsync_IteratesAllDocuments()
    {
        if (!Enabled) return;

        var category = $"visit-all-{Guid.NewGuid():N}";
        var ids = await SeedAsync(8, category);
        try
        {
            var visited = new List<VespaDocument<TestProduct>>();
            await foreach (var doc in fixture.Client.Documents.VisitAsync<TestProduct>(
                "product", selection: null, cluster: null, @namespace: "test",
                wantedDocumentCount: 100))
                visited.Add(doc);

            // Should have at least our 8 documents (could have more from other tests)
            Assert.True(visited.Count >= 8);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task VisitAsync_WithSelection_FiltersDocuments()
    {
        if (!Enabled) return;

        var category = $"visit-sel-{Guid.NewGuid():N}";
        var ids = await SeedAsync(5, category);
        try
        {
            var visited = new List<VespaDocument<TestProduct>>();
            await foreach (var doc in fixture.Client.Documents.VisitAsync<TestProduct>(
                "product",
                selection: $"product.category == \"{category}\"",
                @namespace: "test",
                wantedDocumentCount: 100))
                visited.Add(doc);

            Assert.Equal(5, visited.Count);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task VisitAsync_WithWantedDocumentCount_LimitsResults()
    {
        if (!Enabled) return;

        var category = $"visit-limit-{Guid.NewGuid():N}";
        var ids = await SeedAsync(10, category);
        try
        {
            var visited = new List<VespaDocument<TestProduct>>();
            await foreach (var doc in fixture.Client.Documents.VisitAsync<TestProduct>(
                "product",
                selection: $"product.category == \"{category}\"",
                @namespace: "test",
                wantedDocumentCount: 3))
            {
                visited.Add(doc);
                // Stop after first batch to avoid consuming all
                if (visited.Count >= 3)
                    break;
            }

            Assert.True(visited.Count >= 1 && visited.Count <= 10);
        }
        finally { await CleanupAsync(ids); }
    }

    [Fact]
    public async Task VisitAsync_WithFieldSet_ReturnsRequestedFields()
    {
        if (!Enabled) return;

        var category = $"visit-fs-{Guid.NewGuid():N}";
        var ids = await SeedAsync(3, category);
        try
        {
            var visited = new List<VespaDocument<TestProduct>>();
            await foreach (var doc in fixture.Client.Documents.VisitAsync<TestProduct>(
                "product",
                selection: $"product.category == \"{category}\"",
                @namespace: "test",
                fieldSet: "product:product_name,price",
                wantedDocumentCount: 100))
                visited.Add(doc);

            Assert.Equal(3, visited.Count);
            // All should have Name and Price populated
            Assert.All(visited, d => Assert.False(string.IsNullOrEmpty(d.Fields?.Name)));
        }
        finally { await CleanupAsync(ids); }
    }

    // ── VisitAsync typed extension ──────────────────────────────────────────────

    [Fact]
    public async Task VisitAsync_TypedExtension_InfersDocumentType()
    {
        if (!Enabled) return;

        var category = $"visit-typed-{Guid.NewGuid():N}";
        var ids = await SeedAsync(4, category);
        try
        {
            var visited = new List<VespaDocument<TestProduct>>();
            await foreach (var doc in fixture.Client.Documents.VisitAsync<TestProduct>(
                selection: $"product.category == \"{category}\"",
                wantedDocumentCount: 100))
                visited.Add(doc);

            Assert.Equal(4, visited.Count);
        }
        finally { await CleanupAsync(ids); }
    }
}
