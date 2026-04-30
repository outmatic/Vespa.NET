using Moq;
using Vespa.Documents;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Models.Tensors;
using Vespa.Query;
using Vespa.Search;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for VespaDocumentMeta, model-inferring extension methods,
/// YqlBuilder.From&lt;T&gt;(), and lambda field resolution.
/// </summary>
public class ModelMetadataTests
{
    // ── Test models ──────────────────────────────────────────────────────────

    [VespaDocument("music", Namespace = "mynamespace")]
    private record Music
    {
        [VespaField(Name = "artist_name")]
        public string ArtistName { get; init; } = "";

        public int Year { get; init; }                // no attribute → property name

        [VespaField]                                 // attribute without Name → property name
        public double[] Embedding { get; init; } = [];
    }

    [VespaDocument("docs")]                          // no Namespace
    private record Doc
    {
        public string Title { get; init; } = "";
    }

    private class NoAttribute { }                   // missing [VespaDocument]

    // ── VespaDocumentMeta.For<T>() ───────────────────────────────────────────

    [Fact]
    public void For_ReturnsDocumentType()
    {
        var (docType, _) = VespaDocumentMeta.For<Music>();
        Assert.Equal("music", docType);
    }

    [Fact]
    public void For_ReturnsNamespace()
    {
        var (_, ns) = VespaDocumentMeta.For<Music>();
        Assert.Equal("mynamespace", ns);
    }

    [Fact]
    public void For_NullNamespaceWhenNotSet()
    {
        var (_, ns) = VespaDocumentMeta.For<Doc>();
        Assert.Null(ns);
    }

    [Fact]
    public void For_Type_OverloadWorks()
    {
        var (docType, ns) = VespaDocumentMeta.For(typeof(Music));
        Assert.Equal("music", docType);
        Assert.Equal("mynamespace", ns);
    }

    [Fact]
    public void For_ThrowsWhenNoAttribute()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => VespaDocumentMeta.For<NoAttribute>());
        Assert.Contains("NoAttribute", ex.Message);
        Assert.Contains("[VespaDocument", ex.Message);
    }

    [Fact]
    public void For_IsCached_SameInstanceOnRepeatCalls()
    {
        var a = VespaDocumentMeta.For<Doc>();
        var b = VespaDocumentMeta.For<Doc>();
        Assert.Equal(a, b);
    }

    // ── VespaDocumentMeta.FieldName<T>() ─────────────────────────────────────

    [Fact]
    public void FieldName_UsesVespaFieldName()
    {
        var name = VespaDocumentMeta.FieldName<Music>(m => m.ArtistName);
        Assert.Equal("artist_name", name);
    }

    [Fact]
    public void FieldName_FallsBackToPropertyName_WhenNoAttribute()
    {
        var name = VespaDocumentMeta.FieldName<Music>(m => m.Year);
        Assert.Equal("year", name);
    }

    [Fact]
    public void FieldName_FallsBackToPropertyName_WhenAttributeHasNoName()
    {
        // [VespaField] without Name → camelCase property name (matches schema convention)
        var name = VespaDocumentMeta.FieldName<Music>(m => m.Embedding);
        Assert.Equal("embedding", name);
    }

    [Fact]
    public void FieldName_ThrowsForNonMemberExpression()
    {
        Assert.Throws<ArgumentException>(() =>
            VespaDocumentMeta.FieldName<Music>(m => "literal"));
    }

    // ── YqlBuilder.From<T>() ─────────────────────────────────────────────────

    [Fact]
    public void YqlBuilder_FromT_EmitsDocumentType()
    {
        var yql = YqlBuilder.Select().From<Music>().Build();
        Assert.Contains("from music", yql);
    }

    [Fact]
    public void YqlBuilder_FromT_DocWithoutNamespace()
    {
        var yql = YqlBuilder.Select("title").From<Doc>().Build();
        Assert.Contains("from docs", yql);
    }

    [Fact]
    public void YqlBuilder_FromT_ThrowsForUndecorated()
    {
        Assert.Throws<InvalidOperationException>(() =>
            YqlBuilder.Select().From<NoAttribute>().Build());
    }

    // ── YqlBuilder<T>.Select(lambdas) — typed entry point ────────────────────

    [Fact]
    public void TypedSelect_NoArgs_SelectsStar()
    {
        var yql = YqlBuilder<Music>.Select().Build();
        Assert.StartsWith("select * from music", yql);
    }

    [Fact]
    public void TypedSelect_SingleField_UsesVespaFieldName()
    {
        var yql = YqlBuilder<Music>.Select(m => m.ArtistName).Build();
        Assert.StartsWith("select artist_name from music", yql);
    }

    [Fact]
    public void TypedSelect_MultipleFields_JoinsWithComma()
    {
        var yql = YqlBuilder<Music>.Select(m => m.ArtistName, m => m.Year).Build();
        Assert.StartsWith("select artist_name, year from music", yql);
    }

    [Fact]
    public void TypedSelect_WithWhere_ProducesFullQuery()
    {
        var yql = YqlBuilder<Music>
            .Select(m => m.ArtistName)
            .Where(w => w.Field(m => m.Year).GreaterThan(2000))
            .Build();

        Assert.Equal("select artist_name from music where year > 2000", yql);
    }

    // ── YqlWhereClause.Field<T>(lambda) ───────────────────────────────────────

    [Fact]
    public void WhereField_Lambda_UsesVespaFieldName()
    {
        // T is inferred from From<Music>() — no explicit <Music> needed on Field()
        var yql = YqlBuilder.Select().From<Music>()
            .Where(w => w.Field(m => m.ArtistName).Contains("Beatles"))
            .Build();

        Assert.Contains("artist_name contains", yql);
    }

    [Fact]
    public void WhereField_Lambda_FallsBackToPropertyName()
    {
        var yql = YqlBuilder.Select().From<Music>()
            .Where(w => w.Field(m => m.Year).GreaterThan(2000))
            .Build();

        Assert.Contains("year >", yql);
    }

    [Fact]
    public void WhereNearestNeighbor_Lambda_ResolvesFieldName()
    {
        // T is inferred — no explicit <Music> needed on NearestNeighbor()
        var yql = YqlBuilder.Select().From<Music>()
            .Where(w => w.NearestNeighbor(m => m.Embedding, "query_emb", targetHits: 5))
            .Build();

        // No [VespaField(Name)] → camelCase "embedding"
        Assert.Contains("nearestNeighbor(embedding, query_emb)", yql);
        Assert.Contains("targetHits:5", yql);
    }

    // ── DocumentOperationsExtensions ─────────────────────────────────────────

    [Fact]
    public async Task PutAsync_Extension_PassesInferredDocTypeAndNamespace()
    {
        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.PutAsync("id1", It.IsAny<Music>(), "music", "mynamespace",
                null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        await mock.Object.PutAsync("id1", new Music());

        mock.Verify(m => m.PutAsync("id1", It.IsAny<Music>(), "music", "mynamespace",
            null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAsync_Extension_PassesInferredDocTypeAndNamespace()
    {
        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.GetAsync<Music>("id1", "music", "mynamespace",
                null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaDocument<Music> { Id = "id1" });

        await mock.Object.GetAsync<Music>("id1");

        mock.Verify(m => m.GetAsync<Music>("id1", "music", "mynamespace",
            null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_Extension_PassesInferredDocTypeAndNamespace()
    {
        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.DeleteAsync("id1", "music", "mynamespace",
                null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        await mock.Object.DeleteAsync<Music>("id1");

        mock.Verify(m => m.DeleteAsync("id1", "music", "mynamespace",
            null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateFieldsAsync_Extension_PassesInferredDocType()
    {
        var fieldOps = new Dictionary<string, FieldOperation>
        {
            ["Year"] = FieldOp.Increment()
        };

        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.UpdateFieldsAsync("id1", fieldOps, "music", "mynamespace",
                false, null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        await mock.Object.UpdateFieldsAsync<Music>("id1", fieldOps);

        mock.Verify(m => m.UpdateFieldsAsync("id1", fieldOps, "music", "mynamespace",
            false, null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void VisitAsync_Extension_PassesInferredDocType()
    {
        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.VisitAsync<Music>("music", null, null, "mynamespace",
                null, null, null, null, null, null, null, null, null, null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<VespaDocument<Music>>());

        // Use named arg to avoid ambiguity with the explicit overload
        _ = mock.Object.VisitAsync<Music>(selection: null);

        mock.Verify(m => m.VisitAsync<Music>("music", null, null, "mynamespace",
            null, null, null, null, null, null, null, null, null, null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateFieldsAsync_TypedBuilder_ResolvesFieldNamesViaAttribute()
    {
        Dictionary<string, FieldOperation>? captured = null;

        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.UpdateFieldsAsync("id1", It.IsAny<Dictionary<string, FieldOperation>>(),
                "music", "mynamespace", false, null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Dictionary<string, FieldOperation>, string, string?, bool, string?, DocumentRequestOptions?, CancellationToken>(
                (_, ops, _, _, _, _, _, _) => captured = ops)
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        await mock.Object.UpdateFieldsAsync<Music>("id1", ops => ops
            .Field(m => m.ArtistName, FieldOp.Assign("Beatles"))
            .Field(m => m.Year, FieldOp.Increment()));

        Assert.NotNull(captured);
        Assert.True(captured.ContainsKey("artist_name"), "Expected [VespaField(Name=\"artist_name\")]");
        Assert.True(captured.ContainsKey("year"), "Expected camelCase fallback for property name");
        Assert.Equal(2, captured.Count);
    }

    [Fact]
    public async Task GetByGroupAsync_Extension_PassesInferredDocTypeAndNamespace()
    {
        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.GetByGroupAsync<Music>("g1", "lid1", "music", "mynamespace",
                null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaDocument<Music> { Id = "lid1" });

        await mock.Object.GetByGroupAsync<Music>("g1", "lid1");

        mock.Verify(m => m.GetByGroupAsync<Music>("g1", "lid1", "music", "mynamespace",
            null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetByNumberAsync_Extension_PassesInferredDocTypeAndNamespace()
    {
        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.GetByNumberAsync<Music>(42L, "lid1", "music", "mynamespace",
                null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaDocument<Music> { Id = "lid1" });

        await mock.Object.GetByNumberAsync<Music>(42L, "lid1");

        mock.Verify(m => m.GetByNumberAsync<Music>(42L, "lid1", "music", "mynamespace",
            null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── TypedFieldUpdateBuilder<T> ────────────────────────────────────────────
    // Build() is internal; behavior is verified via the UpdateFieldsAsync extension above.

    [Fact]
    public async Task TypedFieldUpdateBuilder_LastWriteWins_ForSameProperty()
    {
        Dictionary<string, FieldOperation>? captured = null;

        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.UpdateFieldsAsync("id1", It.IsAny<Dictionary<string, FieldOperation>>(),
                "music", "mynamespace", false, null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Dictionary<string, FieldOperation>, string, string?, bool, string?, DocumentRequestOptions?, CancellationToken>(
                (_, ops, _, _, _, _, _, _) => captured = ops)
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        // Same property assigned twice — last value wins.
        await mock.Object.UpdateFieldsAsync<Music>("id1", ops => ops
            .Field(m => m.Year, FieldOp.Increment())
            .Field(m => m.Year, FieldOp.Increment(5)));

        Assert.NotNull(captured);
        Assert.Single(captured);
        Assert.Equal(new FieldOperation("increment", 5.0), captured["year"]);
    }

    // ── UpdateFieldsByGroup/ByNumber/BySelection extensions ───────────────────

    [Fact]
    public async Task UpdateFieldsByGroupAsync_TypedBuilder_ResolvesFieldNamesAndInfersMeta()
    {
        Dictionary<string, FieldOperation>? captured = null;

        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.UpdateFieldsByGroupAsync("g1", "lid1", It.IsAny<Dictionary<string, FieldOperation>>(),
                "music", "mynamespace", false, null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, Dictionary<string, FieldOperation>, string, string?, bool, string?, DocumentRequestOptions?, CancellationToken>(
                (_, _, ops, _, _, _, _, _, _) => captured = ops)
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        await mock.Object.UpdateFieldsByGroupAsync<Music>("g1", "lid1", ops => ops
            .Field(m => m.ArtistName, FieldOp.Assign("Beatles"))
            .Field(m => m.Year, FieldOp.Increment()));

        Assert.NotNull(captured);
        Assert.Equal(2, captured.Count);
        Assert.True(captured.ContainsKey("artist_name"));
        Assert.True(captured.ContainsKey("year"));
    }

    [Fact]
    public async Task UpdateFieldsByGroupAsync_Dictionary_InfersDocTypeAndNamespace()
    {
        var fieldOps = new Dictionary<string, FieldOperation> { ["year"] = FieldOp.Increment() };

        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.UpdateFieldsByGroupAsync("g1", "lid1", fieldOps,
                "music", "mynamespace", false, null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        await mock.Object.UpdateFieldsByGroupAsync<Music>("g1", "lid1", fieldOps);

        mock.Verify(m => m.UpdateFieldsByGroupAsync("g1", "lid1", fieldOps,
            "music", "mynamespace", false, null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateFieldsByNumberAsync_TypedBuilder_ResolvesFieldNamesAndInfersMeta()
    {
        Dictionary<string, FieldOperation>? captured = null;

        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.UpdateFieldsByNumberAsync(42L, "lid1", It.IsAny<Dictionary<string, FieldOperation>>(),
                "music", "mynamespace", false, null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<long, string, Dictionary<string, FieldOperation>, string, string?, bool, string?, DocumentRequestOptions?, CancellationToken>(
                (_, _, ops, _, _, _, _, _, _) => captured = ops)
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        await mock.Object.UpdateFieldsByNumberAsync<Music>(42L, "lid1", ops => ops
            .Field(m => m.ArtistName, FieldOp.Assign("Beatles")));

        Assert.NotNull(captured);
        Assert.Single(captured);
        Assert.True(captured.ContainsKey("artist_name"));
    }

    [Fact]
    public async Task UpdateFieldsByNumberAsync_Dictionary_InfersDocTypeAndNamespace()
    {
        var fieldOps = new Dictionary<string, FieldOperation> { ["year"] = FieldOp.Increment() };

        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.UpdateFieldsByNumberAsync(42L, "lid1", fieldOps,
                "music", "mynamespace", false, null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        await mock.Object.UpdateFieldsByNumberAsync<Music>(42L, "lid1", fieldOps);

        mock.Verify(m => m.UpdateFieldsByNumberAsync(42L, "lid1", fieldOps,
            "music", "mynamespace", false, null, It.IsAny<DocumentRequestOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateBySelectionAsync_TypedBuilder_ResolvesFieldNamesAndInfersMeta()
    {
        Dictionary<string, FieldOperation>? captured = null;

        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.UpdateBySelectionAsync("music.year < 2000", It.IsAny<Dictionary<string, FieldOperation>>(),
                "music", "mynamespace", "content", It.IsAny<SelectionRequestOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Dictionary<string, FieldOperation>, string, string?, string?, SelectionRequestOptions?, CancellationToken>(
                (_, ops, _, _, _, _, _) => captured = ops)
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        await mock.Object.UpdateBySelectionAsync<Music>(
            "music.year < 2000",
            ops => ops.Field(m => m.ArtistName, FieldOp.Assign("archived")),
            cluster: "content");

        Assert.NotNull(captured);
        Assert.Single(captured);
        Assert.True(captured.ContainsKey("artist_name"));
    }

    [Fact]
    public async Task UpdateBySelectionAsync_Dictionary_InfersDocTypeAndNamespace()
    {
        var fieldOps = new Dictionary<string, FieldOperation> { ["year"] = FieldOp.Increment() };

        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.UpdateBySelectionAsync("music.year < 2000", fieldOps,
                "music", "mynamespace", null, It.IsAny<SelectionRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaResponse { IsSuccess = true });

        await mock.Object.UpdateBySelectionAsync<Music>("music.year < 2000", fieldOps);

        mock.Verify(m => m.UpdateBySelectionAsync("music.year < 2000", fieldOps,
            "music", "mynamespace", null, It.IsAny<SelectionRequestOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateBySelectionPageAsync_TypedBuilder_ResolvesFieldNamesAndInfersMeta()
    {
        Dictionary<string, FieldOperation>? captured = null;

        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.UpdateBySelectionPageAsync("music.year < 2000", It.IsAny<Dictionary<string, FieldOperation>>(),
                "music", "mynamespace", null, "tok", It.IsAny<SelectionRequestOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Dictionary<string, FieldOperation>, string, string?, string?, string?, SelectionRequestOptions?, CancellationToken>(
                (_, ops, _, _, _, _, _, _) => captured = ops)
            .ReturnsAsync(new SelectionPageResult());

        await mock.Object.UpdateBySelectionPageAsync<Music>(
            "music.year < 2000",
            ops => ops.Field(m => m.Year, FieldOp.Increment()),
            continuation: "tok");

        Assert.NotNull(captured);
        Assert.Single(captured);
        Assert.True(captured.ContainsKey("year"));
    }

    [Fact]
    public async Task UpdateBySelectionPageAsync_Dictionary_InfersDocTypeAndNamespace()
    {
        var fieldOps = new Dictionary<string, FieldOperation> { ["year"] = FieldOp.Increment() };

        var mock = new Mock<IDocumentOperations>();
        mock.Setup(m => m.UpdateBySelectionPageAsync("music.year < 2000", fieldOps,
                "music", "mynamespace", null, null, It.IsAny<SelectionRequestOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SelectionPageResult());

        await mock.Object.UpdateBySelectionPageAsync<Music>("music.year < 2000", fieldOps);

        mock.Verify(m => m.UpdateBySelectionPageAsync("music.year < 2000", fieldOps,
            "music", "mynamespace", null, null, It.IsAny<SelectionRequestOptions?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── SearchOperationsExtensions ────────────────────────────────────────────

    [Fact]
    public async Task NearestNeighborSearchAsync_Extension_InfersDocType()
    {
        var embedding = VespaTensor.FromDenseValues([0.1f, 0.2f, 0.3f]);

        var mock = new Mock<ISearchOperations>();
        mock.Setup(m => m.NearestNeighborSearchAsync<Music>(
                embedding, "my_field", "music", 10, null, null, "mynamespace",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaSearchResponse<Music>());

        await mock.Object.NearestNeighborSearchAsync<Music>(embedding, "my_field");

        mock.Verify(m => m.NearestNeighborSearchAsync<Music>(
            embedding, "my_field", "music", 10, null, null, "mynamespace",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NearestNeighborSearchAsync_LambdaExtension_ResolvesFieldAndDocType()
    {
        var embedding = VespaTensor.FromDenseValues([0.1f, 0.2f, 0.3f]);

        var mock = new Mock<ISearchOperations>();
        mock.Setup(m => m.NearestNeighborSearchAsync<Music>(
                embedding, "embedding", "music", 5, null, null, "mynamespace",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaSearchResponse<Music>());

        // No [VespaField(Name)] → camelCase "embedding"
        await mock.Object.NearestNeighborSearchAsync<Music>(embedding, m => m.Embedding, topK: 5);

        mock.Verify(m => m.NearestNeighborSearchAsync<Music>(
            embedding, "embedding", "music", 5, null, null, "mynamespace",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NearestNeighborSearchAsync_LambdaExtension_UsesVespaFieldName()
    {
        var embedding = VespaTensor.FromDenseValues([0.1f, 0.2f]);

        var mock = new Mock<ISearchOperations>();
        mock.Setup(m => m.NearestNeighborSearchAsync<Music>(
                embedding, "artist_name", "music", 10, null, null, "mynamespace",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VespaSearchResponse<Music>());

        // [VespaField(Name = "artist_name")]
        await mock.Object.NearestNeighborSearchAsync<Music>(embedding, m => m.ArtistName);

        mock.Verify(m => m.NearestNeighborSearchAsync<Music>(
            embedding, "artist_name", "music", 10, null, null, "mynamespace",
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
