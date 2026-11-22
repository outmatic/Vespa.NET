using System.Linq.Expressions;
using System.Net;
using System.Net.Http.Headers;
using Vespa.Documents;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class TypedVisitTests : IDisposable
{
    private const string ResumeToken = "opaque+/= &?";
    private readonly MockHttpMessageHandler _handler = new();
    private readonly HttpClient _http;
    private readonly IDocumentOperations _documents;

    public TypedVisitTests()
    {
        _http = new(_handler) { BaseAddress = new Uri("http://localhost:8080") };
        _documents = new DocumentOperations(_http, new() { DefaultNamespace = "fallback" });
    }

    public void Dispose() => _http.Dispose();

    [Fact]
    public void SelectionFactory_ScopesTheEntirePredicateIncludingConstantsAndNegation()
    {
        Assert.Equal("(inventory and (true))", DocumentSelection.For<Inventory>(p => true));
        Assert.Equal("(inventory and (false))", DocumentSelection.For<Inventory>(p => false));
        Assert.Equal("(inventory and (not ((inventory.unit_price < 10))))", DocumentSelection.For<Inventory>(p => !(p.Price < 10)));
        Assert.Equal("(inventory and (((inventory.unit_price > 10) or (inventory.active == true))))",
            DocumentSelection.For<Inventory>(p => p.Price > 10 || p.Active));
        Assert.Equal("true", DocumentCondition.For<Inventory>(p => true));
    }

    [Fact]
    public void SelectionFactory_FreezesValuesAndSupportsUnattributedExplicitTargets()
    {
        var threshold = 10;
        var selection = DocumentSelection.For<Projection>("other_schema", p => p.Price > threshold);
        threshold = 20;
        Assert.Equal("(other_schema and ((other_schema.unit_price > 10)))", selection);
        Assert.Equal("(other_schema and ((other_schema.unit_price > 20)))",
            DocumentSelection.For<Inventory>("other_schema", p => p.Price > threshold));
        Assert.Throws<ArgumentNullException>(() => DocumentSelection.For<Inventory>(null!));
        Assert.Throws<ArgumentNullException>(() => DocumentSelection.For<Projection>("inventory", null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("product or true")]
    [InlineData("id")]
    public void SelectionFactory_RejectsInvalidExplicitTypes(string documentType) =>
        Assert.ThrowsAny<ArgumentException>(() => DocumentSelection.For<Projection>(documentType, p => true));

    [Theory]
    [InlineData("paged", null, null, "docid/")]
    [InlineData("page", "team one", null, "group/team%20one")]
    [InlineData("jsonl", null, 42L, "number/42")]
    public async Task ModelVisits_PreserveTypedSelectionAddressingAndOptions(string mode, string? group, long? number, string suffix)
    {
        Queue(mode);
        var options = new VisitOptions
        {
            Group = group,
            Number = number,
            BucketSpace = "global",
            FieldSet = "inventory:unit_price",
            Slices = 3,
            SliceId = 1,
            WantedDocumentCount = 7
        };
        await ReadModel(mode, p => p.Price > 10, options: options);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("/document/v1/tenant/inventory/" + suffix, request.RequestUri!.AbsolutePath);
        var query = Query(request.RequestUri);
        Assert.Equal("(inventory and ((inventory.unit_price > 10)))", query["selection"]);
        Assert.Equal("content", query["cluster"]);
        Assert.Equal("global", query["bucketSpace"]);
        Assert.Equal(options.FieldSet, query["fieldSet"]);
        Assert.Equal("3", query["slices"]);
        Assert.Equal("1", query["sliceId"]);
        if (mode == "jsonl")
        {
            Assert.Equal("true", query["stream"]);
            Assert.DoesNotContain("wantedDocumentCount", query);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/jsonl");
        }
        else
            Assert.Equal("7", query["wantedDocumentCount"]);
    }

    [Theory]
    [InlineData("paged", null)]
    [InlineData("page", null)]
    [InlineData("jsonl", null)]
    [InlineData("paged", "true")]
    [InlineData("page", "inventory.unit_price > 10")]
    [InlineData("jsonl", "inventory.unit_price > 10")]
    public async Task ModelVisits_RawAndOmittedSelectionsKeepNativeMeaning(string mode, string? rawSelection)
    {
        Queue(mode);
        await ReadModel(mode, rawSelection: rawSelection);
        var query = Query(Assert.Single(_handler.Requests).RequestUri!);
        if (rawSelection is null)
            Assert.DoesNotContain("selection", query);
        else
            Assert.Equal(rawSelection, query["selection"]);
    }

    [Theory]
    [InlineData("paged")]
    [InlineData("page")]
    [InlineData("jsonl")]
    public void ModelVisits_RejectConflictsAndUnsupportedExpressionsBeforeEnumeration(string mode)
    {
        Assert.Throws<ArgumentException>(() => Create(p => true, "true"));
        Assert.Throws<NotSupportedException>(() => Create(p => p.Name.StartsWith("prefix"), null));
        Assert.Throws<NotSupportedException>(() => Create(p => p.Id == "item", null));
        Assert.Empty(_handler.Requests);

        object Create(Expression<Func<Inventory, bool>> selection, string? raw) => mode switch
        {
            "page" => _documents.VisitPageAsync(selection: selection, rawSelection: raw),
            "jsonl" => _documents.VisitJsonlAsync(selection: selection, rawSelection: raw),
            _ => _documents.VisitAsync(selection: selection, rawSelection: raw)
        };
    }

    [Theory]
    [InlineData("paged")]
    [InlineData("jsonl")]
    public async Task ModelVisits_FreezeTheSelectionBeforeEnumerationAndAcrossChunks(string mode)
    {
        Queue(mode, firstDocument: true, continuation: ResumeToken);
        Queue(mode);
        var threshold = 10;
        var visit = mode == "jsonl"
            ? _documents.VisitJsonlAsync<Inventory>(selection: p => p.Price > threshold)
            : _documents.VisitAsync<Inventory>(selection: p => p.Price > threshold);
        threshold = 20;
        Assert.Empty(_handler.Requests);
        await using var iterator = visit.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal("first", iterator.Current.Id);
        threshold = 30;
        Assert.False(await iterator.MoveNextAsync());
        Assert.Equal(2, _handler.Requests.Count);
        var first = Query(_handler.Requests[0].RequestUri!);
        var last = Query(_handler.Requests[1].RequestUri!);
        Assert.Equal("(inventory and ((inventory.unit_price > 10)))", first["selection"]);
        Assert.Equal(first["selection"], last["selection"]);
        Assert.DoesNotContain("continuation", first);
        Assert.Equal(ResumeToken, last["continuation"]);
    }

    [Fact]
    public async Task ManualResume_CanReuseAFrozenSelectorAfterCapturedValuesChange()
    {
        Queue("page", continuation: ResumeToken);
        Queue("page");
        var threshold = 10;
        var frozen = DocumentSelection.For<Inventory>(p => p.Price > threshold);
        var first = await _documents.VisitPageAsync<Inventory>(rawSelection: frozen);
        Assert.Empty(first.Documents);
        Assert.False(first.IsComplete);
        threshold = 20;
        var last = await _documents.VisitPageAsync<Inventory>(rawSelection: frozen, continuation: first.Continuation);
        Assert.True(last.IsComplete);
        Assert.Equal(frozen, Query(_handler.Requests[0].RequestUri!)["selection"]);
        var resumed = Query(_handler.Requests[1].RequestUri!);
        Assert.Equal(frozen, resumed["selection"]);
        Assert.Equal(ResumeToken, resumed["continuation"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RootVisits_SeparateFilterMetadataFromResultShape(bool page)
    {
        Queue("page", firstDocument: true);
        var options = new VisitOptions { BucketSpace = "global" };
        var documents = page
            ? (await _documents.VisitAllPageAsync<Projection, Inventory>("content", p => p.Price > 10,
                continuation: ResumeToken, options: options)).Documents
            : await Collect(_documents.VisitAllAsync<Projection, Inventory>("content", p => p.Price > 10, options));
        Assert.Equal(12, Assert.Single(documents).Fields!.Price);
        var uri = Assert.Single(_handler.Requests).RequestUri!;
        Assert.Equal("/document/v1/", uri.AbsolutePath);
        var query = Query(uri);
        Assert.Equal("(inventory and ((inventory.unit_price > 10)))", query["selection"]);
        Assert.Equal("content", query["cluster"]);
        Assert.Equal("global", query["bucketSpace"]);
        if (page)
            Assert.Equal(ResumeToken, query["continuation"]);
    }

    [Fact]
    public async Task TypedRootVisit_FreezesTheFilterBeforeEnumerationAndAcrossPages()
    {
        Queue("page", firstDocument: true, continuation: ResumeToken);
        Queue("page");
        var threshold = 10;
        var visit = _documents.VisitAllAsync<Projection, Inventory>("content", p => p.Price > threshold);
        threshold = 20;
        Assert.Empty(_handler.Requests);
        await using var iterator = visit.GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        threshold = 30;
        Assert.False(await iterator.MoveNextAsync());
        Assert.Equal(2, _handler.Requests.Count);
        Assert.All(_handler.Requests, request =>
        {
            Assert.Equal("/document/v1/", request.RequestUri!.AbsolutePath);
            Assert.Equal("(inventory and ((inventory.unit_price > 10)))", Query(request.RequestUri)["selection"]);
        });
        Assert.Equal(ResumeToken, Query(_handler.Requests[1].RequestUri!)["continuation"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeRootVisits_RemainUnrestrictedAndAcceptUnattributedResults(bool page)
    {
        Queue("page");
        if (page)
            await _documents.VisitAllPageAsync<Projection>("content", selection: "true");
        else
            await Collect(_documents.VisitAllAsync<Projection>("content", selection: "true"));
        Assert.Equal("true", Query(Assert.Single(_handler.Requests).RequestUri!)["selection"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TypedRootVisits_KeepNativeAddressValidationAndRequireAPredicate(bool page)
    {
        Assert.Throws<ArgumentNullException>(() => Create("content", null!, null));
        Assert.ThrowsAny<ArgumentException>(() => Create("", p => true, null));
        Assert.Throws<ArgumentException>(() => Create("content", p => true, new() { Group = "team" }));
        Assert.Throws<ArgumentException>(() => Create("content", p => true, new() { Number = 42 }));
        Assert.Empty(_handler.Requests);

        object Create(string cluster, Expression<Func<Inventory, bool>> selection, VisitOptions? options) => page
            ? _documents.VisitAllPageAsync<Projection, Inventory>(cluster, selection, options: options)
            : _documents.VisitAllAsync<Projection, Inventory>(cluster, selection, options);
    }

    [Theory]
    [InlineData("paged")]
    [InlineData("page")]
    [InlineData("jsonl")]
    public async Task ExplicitTargets_UseTheFactoryTargetAndNativeNamespace(string mode)
    {
        Queue(mode);
        var selection = DocumentSelection.For<Projection>("other_schema", p => p.Price > 10);
        if (mode == "page")
            await _documents.VisitPageAsync<Projection>("other_schema", selection, @namespace: "other_tenant");
        else
            await Collect(mode == "jsonl"
                ? _documents.VisitJsonlAsync<Projection>("other_schema", selection, @namespace: "other_tenant")
                : _documents.VisitAsync<Projection>("other_schema", selection, @namespace: "other_tenant"));
        var uri = Assert.Single(_handler.Requests).RequestUri!;
        Assert.Equal("/document/v1/other_tenant/other_schema/docid/", uri.AbsolutePath);
        Assert.Equal(selection, Query(uri)["selection"]);
    }

    [Theory]
    [InlineData("paged")]
    [InlineData("page")]
    [InlineData("jsonl")]
    [InlineData("root")]
    [InlineData("root-page")]
    public async Task TypedVisits_PropagateCancellation(string mode)
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (mode == "root-page")
                await _documents.VisitAllPageAsync<Projection, Inventory>("content", p => true, cancellationToken: source.Token);
            else if (mode == "root")
                await Collect(_documents.VisitAllAsync<Projection, Inventory>("content", p => true, cancellationToken: source.Token));
            else
                await ReadModel(mode, p => true, cancellationToken: source.Token);
        });
    }

    private async Task ReadModel(string mode, Expression<Func<Inventory, bool>>? selection = null,
        string? rawSelection = null, VisitOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (mode == "page")
            await _documents.VisitPageAsync(selection: selection, rawSelection: rawSelection, cluster: "content",
                options: options, cancellationToken: cancellationToken);
        else
            await Collect(mode == "jsonl"
                ? _documents.VisitJsonlAsync(selection: selection, rawSelection: rawSelection, cluster: "content", options: options, cancellationToken: cancellationToken)
                : _documents.VisitAsync(selection: selection, rawSelection: rawSelection, cluster: "content", options: options, cancellationToken: cancellationToken));
    }

    private void Queue(string mode, bool firstDocument = false, string? continuation = null)
    {
        var body = mode == "jsonl"
            ? (firstDocument ? "{\"put\":\"id:tenant:inventory::first\",\"fields\":{\"unit_price\":12}}\n" : "") +
                (continuation is null ? "{\"continuation\":{\"percentFinished\":100}}" : $"{{\"continuation\":{{\"token\":\"{continuation}\"}}}}")
            : "{\"documents\":[" + (firstDocument ? "{\"id\":\"id:tenant:inventory::first\",\"fields\":{\"unit_price\":12}}" : "") + "]" +
                (continuation is null ? "}" : $",\"continuation\":\"{continuation}\"}}");
        var content = new StringContent(body);
        if (mode == "jsonl")
            content.Headers.ContentType = new MediaTypeHeaderValue("application/jsonl");
        _handler.QueueResponse(new(HttpStatusCode.OK) { Content = content });
    }

    private static async Task<List<VespaDocument<T>>> Collect<T>(IAsyncEnumerable<VespaDocument<T>> source) where T : class
    {
        List<VespaDocument<T>> documents = [];
        await foreach (var document in source)
            documents.Add(document);
        return documents;
    }

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

    [VespaDocument("inventory", Namespace = "tenant")]
    private sealed class Inventory
    {
        [VespaId]
        public string? Id { get; init; }
        [VespaField(Name = "unit_price")]
        public int Price { get; init; }
        public bool Active { get; init; }
        public string Name { get; init; } = "";
    }

    private sealed class Projection
    {
        [VespaField(Name = "unit_price")]
        public int Price { get; init; }
    }
}
