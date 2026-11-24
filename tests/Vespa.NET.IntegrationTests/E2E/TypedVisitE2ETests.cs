using System.Linq.Expressions;
using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
using Vespa.Models.Attributes;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

[Collection("Vespa")]
[Trait("Category", "E2E")]
public sealed class TypedVisitE2ETests(VespaFixture fixture)
{
    [Theory]
    [InlineData("paged")]
    [InlineData("page")]
    [InlineData("jsonl")]
    public async Task ModelVisits_FilterRealDocumentsAndFreezeCapturedValues(string mode)
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var tag = $"typed-visit-{Guid.NewGuid():N}";
        var ids = Enumerable.Range(0, 3).Select(i => $"{tag}-{i}").ToArray();
        try
        {
            await documents.PutAsync(ids[0], new TestProduct { Name = "First", Category = tag, Price = 10 });
            await documents.PutAsync(ids[1], new TestProduct { Name = "Second", Category = tag, Price = 20 });
            await documents.PutAsync(ids[2], new TestProduct { Name = "Other", Category = tag + "-other", Price = 30 });
            var category = tag;
            var options = new VisitOptions { WantedDocumentCount = 1 };
            List<VespaDocument<TestProduct>> found = [];
            if (mode == "page")
            {
                var frozen = DocumentSelection.For<TestProduct>(p => p.Category == category);
                var page = await documents.VisitPageAsync<TestProduct>(selection: p => p.Category == category,
                    cluster: "content", options: options);
                found.AddRange(page.Documents);
                category = "changed-after-first-page";
                while (!page.IsComplete)
                {
                    page = await documents.VisitPageAsync<TestProduct>(rawSelection: frozen, cluster: "content",
                        continuation: page.Continuation, options: options);
                    found.AddRange(page.Documents);
                }
            }
            else
            {
                var visit = mode == "jsonl"
                    ? documents.VisitJsonlAsync<TestProduct>(selection: p => p.Category == category, cluster: "content", options: options)
                    : documents.VisitAsync<TestProduct>(selection: p => p.Category == category, cluster: "content", options: options);
                category = "changed-before-enumeration";
                await foreach (var document in visit)
                {
                    found.Add(document);
                    category = "changed-after-document";
                }
            }

            Assert.Equal(ids.Take(2).Order(), found.Select(p => p.Id).Order());
            Assert.All(found, p => Assert.Equal(tag, p.Fields!.Category));
        }
        finally
        {
            foreach (var id in ids)
                await documents.DeleteAsync<TestProduct>(id);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task RootVisits_KeepTrueAndNegationWithinTheExactTypeAcrossNamespaces(bool pageMode, bool selectAll)
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var tag = $"typed-root-{Guid.NewGuid():N}";
        var ids = new[] { tag + "-base", tag + "-other-namespace", tag + "-child", tag + "-product" };
        try
        {
            await documents.PutAsync(ids[0], new BaseItem { Title = tag, BaseScore = 20 });
            await documents.PutAsync(ids[1], new BaseItem { Title = tag, BaseScore = 30 },
                documentType: "base_item", @namespace: "other");
            await documents.PutAsync(ids[2], new SpecialItem { Title = tag, BaseScore = 40, SpecialNote = tag });
            await documents.PutAsync(ids[3], new TestProduct { Name = tag, Category = tag });

            Expression<Func<BaseItem, bool>> selection = selectAll ? p => true : p => !(p.BaseScore < 10);
            List<VespaDocument<RootProjection>> found = [];
            if (pageMode)
            {
                string? continuation = null;
                do
                {
                    var page = await documents.VisitAllPageAsync<RootProjection, BaseItem>("content", selection,
                        continuation: continuation, options: new() { WantedDocumentCount = 1 });
                    found.AddRange(page.Documents);
                    continuation = page.Continuation;
                }
                while (continuation is not null);
            }
            else
                await foreach (var document in documents.VisitAllAsync<RootProjection, BaseItem>("content", selection))
                    found.Add(document);

            var seeded = found.Where(p => ids.Contains(p.Id)).ToArray();
            Assert.Equal(ids.Take(2).Order(), seeded.Select(p => p.Id).Order());
            Assert.All(seeded, p => Assert.Equal(tag, p.Fields!.Title));
            Assert.Contains(seeded, p => p.FullId.StartsWith("id:other:base_item:", StringComparison.Ordinal));
        }
        finally
        {
            await documents.DeleteAsync<BaseItem>(ids[0]);
            await documents.DeleteAsync(ids[1], documentType: "base_item", @namespace: "other");
            await documents.DeleteAsync<SpecialItem>(ids[2]);
            await documents.DeleteAsync<TestProduct>(ids[3]);
        }
    }

    [Fact]
    public async Task ExplicitSelectionFactory_UsesTheNativeTargetWithoutAModelAttribute()
    {
        if (!VespaFixture.IntegrationEnabled) return;
        var documents = fixture.Client.Documents;
        var id = $"typed-explicit-visit-{Guid.NewGuid():N}";
        try
        {
            await documents.PutAsync(id, new TestProduct { Name = "Explicit", Category = id });
            var selector = DocumentSelection.For<ExplicitProduct>("product", p => p.Category == id);
            List<VespaDocument<ExplicitProduct>> found = [];
            await foreach (var document in documents.VisitAsync<ExplicitProduct>("product", selector, cluster: "content", @namespace: "test"))
                found.Add(document);
            var result = Assert.Single(found);
            Assert.Equal(id, result.Id);
            Assert.Equal("Explicit", result.Fields!.Name);
        }
        finally
        {
            await documents.DeleteAsync<TestProduct>(id);
        }
    }

    private sealed class RootProjection
    {
        public string Title { get; init; } = "";
        public long BaseScore { get; init; }
    }

    private sealed class ExplicitProduct
    {
        [VespaField(Name = "product_name")]
        public string Name { get; init; } = "";
        public string Category { get; init; } = "";
    }
}
