using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Models.Attributes;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public sealed class TypedSelectionMutationE2ETests(VespaFixture fixture)
{
    private const string TestNamespace = "typed_selection_tests";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TypedUpdate_ChangesOnlyMatchesAndFreezesBeforeTheCallback(bool pageMode, bool builder)
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var tag = $"selection-update-{Guid.NewGuid():N}";
        var ids = Enumerable.Range(0, 3).Select(i => $"{tag}-{i}").ToArray();
        try
        {
            await SeedProducts(documents, ids, tag);
            var threshold = 25;
            var frozen = DocumentSelection.For<MutationProduct>(p => p.Category == tag && p.Price < threshold);
            Dictionary<string, FieldOperation> fields = new() { ["price"] = FieldOp.Increment(100) };
            void Configure(TypedFieldUpdateBuilder<MutationProduct> update)
            {
                threshold = 100;
                update.Field(p => p.Price, FieldOp.Increment(100));
            }

            long count;
            if (pageMode)
            {
                var page = builder
                    ? await documents.UpdateBySelectionPageAsync<MutationProduct>(Configure,
                        selection: p => p.Category == tag && p.Price < threshold, cluster: "content")
                    : await documents.UpdateBySelectionPageAsync<MutationProduct>(fields,
                        selection: p => p.Category == tag && p.Price < threshold, cluster: "content");
                count = page.DocumentCount;
                threshold = 0;
                while (!page.IsComplete)
                {
                    page = builder
                        ? await documents.UpdateBySelectionPageAsync<MutationProduct>(Configure, rawSelection: frozen,
                            cluster: "content", continuation: page.Continuation)
                        : await documents.UpdateBySelectionPageAsync<MutationProduct>(fields, rawSelection: frozen,
                            cluster: "content", continuation: page.Continuation);
                    count += page.DocumentCount;
                }
            }
            else
            {
                var response = builder
                    ? await documents.UpdateBySelectionAsync<MutationProduct>(Configure,
                        selection: p => p.Category == tag && p.Price < threshold, cluster: "content")
                    : await documents.UpdateBySelectionAsync<MutationProduct>(fields,
                        selection: p => p.Category == tag && p.Price < threshold, cluster: "content");
                Assert.True(response.IsSuccess);
                count = response.DocumentCount ?? 0;
            }

            Assert.Equal(2L, count);
            Assert.Equal(110, (await documents.GetAsync<MutationProduct>(ids[0]))?.Fields?.Price);
            Assert.Equal(120, (await documents.GetAsync<MutationProduct>(ids[1]))?.Fields?.Price);
            Assert.Equal(30, (await documents.GetAsync<MutationProduct>(ids[2]))?.Fields?.Price);
        }
        finally
        {
            foreach (var id in ids)
                await documents.DeleteAsync<MutationProduct>(id);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedDelete_RemovesOnlyMatchesAndPreservesTheCount(bool pageMode)
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var tag = $"selection-delete-{Guid.NewGuid():N}";
        var ids = Enumerable.Range(0, 3).Select(i => $"{tag}-{i}").ToArray();
        try
        {
            await SeedProducts(documents, ids, tag);
            long count;
            if (pageMode)
            {
                var saved = DocumentSelection.For<MutationProduct>(p => p.Category == tag && p.Price < 25);
                var page = await documents.DeleteBySelectionPageAsync<MutationProduct>(
                    selection: p => p.Category == tag && p.Price < 25, cluster: "content");
                count = page.DocumentCount;
                while (!page.IsComplete)
                {
                    page = await documents.DeleteBySelectionPageAsync<MutationProduct>(rawSelection: saved,
                        cluster: "content", continuation: page.Continuation);
                    count += page.DocumentCount;
                }
            }
            else
                count = (await documents.DeleteBySelectionAsync<MutationProduct>(
                    selection: p => p.Category == tag && p.Price < 25, cluster: "content")).DocumentCount ?? 0;

            Assert.Equal(2L, count);
            Assert.Null(await documents.GetAsync<MutationProduct>(ids[0]));
            Assert.Null(await documents.GetAsync<MutationProduct>(ids[1]));
            Assert.Equal(30, (await documents.GetAsync<MutationProduct>(ids[2]))?.Fields?.Price);
        }
        finally
        {
            foreach (var id in ids)
                await documents.DeleteAsync<MutationProduct>(id);
        }
    }

    [Fact]
    public async Task NativeAndTypedNoMatchSelections_SucceedWithoutChangingDocuments()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var id = $"selection-no-match-{Guid.NewGuid():N}";
        try
        {
            await documents.PutAsync(id, new MutationProduct { Category = id, Price = 10 });
            var update = await documents.UpdateBySelectionAsync<MutationProduct>(
                fields => fields.Field(p => p.Price, FieldOp.Assign(999)),
                selection: p => p.Category == id && p.Price < 0, cluster: "content");
            var delete = await documents.DeleteBySelectionPageAsync<MutationProduct>(
                rawSelection: $"product.category == \"{id}\" and product.price < 0", cluster: "content");
            Assert.Equal(0L, update.DocumentCount);
            Assert.Equal(0L, delete.DocumentCount);
            Assert.Equal(10, (await documents.GetAsync<MutationProduct>(id))?.Fields?.Price);
        }
        finally
        {
            await documents.DeleteAsync<MutationProduct>(id);
        }
    }

    [Fact]
    public async Task ExplicitTrue_UpdatesAndDeletesOnlyTheAddressedExactType()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var id = $"selection-all-{Guid.NewGuid():N}";
        try
        {
            await documents.PutAsync(id, new MutationBase { Title = id, BaseScore = 10 });
            await documents.PutAsync(id, new MutationChild { Title = id, BaseScore = 20 });
            var update = await documents.UpdateBySelectionAsync<MutationBase>(
                fields => fields.Field(p => p.BaseScore, FieldOp.Assign(99)), selection: p => true, cluster: "content");
            Assert.Equal(1L, update.DocumentCount);
            Assert.Equal(99L, (await documents.GetAsync<MutationBase>(id))?.Fields?.BaseScore);
            Assert.Equal(20L, (await documents.GetAsync<MutationChild>(id))?.Fields?.BaseScore);
            var delete = await documents.DeleteBySelectionAsync<MutationBase>(selection: p => true, cluster: "content");
            Assert.Equal(1L, delete.DocumentCount);
            Assert.Null(await documents.GetAsync<MutationBase>(id));
            Assert.NotNull(await documents.GetAsync<MutationChild>(id));
        }
        finally
        {
            await documents.DeleteAsync<MutationBase>(id);
            await documents.DeleteAsync<MutationChild>(id);
        }
    }

    private static async Task SeedProducts(IDocumentOperations documents, string[] ids, string tag)
    {
        for (var i = 0; i < ids.Length; i++)
            await documents.PutAsync(ids[i], new MutationProduct { Category = tag, Price = (i + 1) * 10 });
    }

    [VespaDocument("product", Namespace = TestNamespace)]
    private sealed record MutationProduct
    {
        public string Category { get; init; } = "";
        public double Price { get; init; }
    }

    [VespaDocument("base_item", Namespace = TestNamespace)]
    private sealed record MutationBase
    {
        public string Title { get; init; } = "";
        public long BaseScore { get; init; }
    }

    [VespaDocument("special_item", Namespace = TestNamespace)]
    private sealed record MutationChild
    {
        public string Title { get; init; } = "";
        public long BaseScore { get; init; }
    }
}
