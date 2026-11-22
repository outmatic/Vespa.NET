using System.Linq.Expressions;
using System.Net;
using System.Text.Json.Serialization;
using System.Web;
using Vespa.Documents;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class DocumentFieldSetTests : IDisposable
{
    private readonly MockHttpMessageHandler _handler = new();
    private readonly HttpClient _http;
    private readonly DocumentOperations _operations;

    public DocumentFieldSetTests()
    {
        _http = new(_handler) { BaseAddress = new Uri("http://localhost:8080") };
        _operations = new(_http, new() { DefaultNamespace = "fallback" });
    }

    public void Dispose() => _http.Dispose();

    [Fact]
    public void FieldListResolvesInheritedMappedAndComplexPropertiesWithoutInvokingGetters()
    {
        Assert.Equal("inventory:unit_price,stock_count,details",
            DocumentFieldSet.For<Inventory>(p => p.Price, p => p.StockCount, p => p.Details));
    }

    [Fact]
    public void ExplicitTypeWorksWithoutMetadataAndOverridesAnAnnotatedType()
    {
        Assert.Throws<InvalidOperationException>(() => DocumentFieldSet.For<Plain>(p => p.Price));
        Assert.Equal("other:unit_price", DocumentFieldSet.ForDocumentType<Plain>("other", p => p.Price));
        Assert.Equal("other:unit_price", DocumentFieldSet.ForDocumentType<Inventory>("other", p => p.Price));
    }

    [Fact]
    public void OrdinalFieldNamesAreDeduplicatedInFirstSeenOrderAndFrozen()
    {
        Expression<Func<Aliases, object?>>[] fields = [p => p.Lower, p => p.Upper, p => p.SameAsLower];
        var fieldSet = DocumentFieldSet.ForDocumentType<Aliases>("id", fields);
        fields[0] = p => p.Upper;
        Assert.Equal("id:and,And", fieldSet);
    }

    [Fact]
    public void EmptyListsNullArraysAndNullElementsFailAtTheFactory()
    {
        Assert.Throws<ArgumentException>(() => DocumentFieldSet.For<Inventory>());
        Assert.Throws<ArgumentException>(() => DocumentFieldSet.ForDocumentType<Plain>("inventory", []));
        Assert.Throws<ArgumentNullException>(() => DocumentFieldSet.For<Inventory>(null!));
        Assert.Throws<ArgumentNullException>(() => DocumentFieldSet.ForDocumentType<Plain>("inventory", default!));
        Assert.Throws<ArgumentNullException>(() => DocumentFieldSet.For<Inventory>(p => p.Price, null!));
        Assert.Throws<ArgumentNullException>(() => DocumentFieldSet.ForDocumentType<Plain>(null!, p => p.Price));
    }

    [Fact]
    public void SelectorsRequireADirectPropertyWithAnIndividualDocumentBinding()
    {
        var other = new Plain();
        Assert.Throws<ArgumentException>(() => DocumentFieldSet.For<Inventory>(p => p.Details.Size));
        Assert.Throws<ArgumentException>(() => DocumentFieldSet.For<Inventory>(p => other.Price));
        Assert.Throws<ArgumentException>(() => DocumentFieldSet.For<Inventory>(p => Math.Abs(p.Price)));
        Assert.Throws<NotSupportedException>(() => DocumentFieldSet.For<Inventory>(p => p.Id));
        Assert.Throws<NotSupportedException>(() => DocumentFieldSet.For<Inventory>(p => p.Ignored));
        Assert.Throws<NotSupportedException>(() => DocumentFieldSet.For<Inventory>(p => p.JsonOnlyAlias));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad,target")]
    [InlineData("bad:target")]
    [InlineData("bad.target")]
    [InlineData("[document]")]
    [InlineData("bad target")]
    [InlineData("1target")]
    [InlineData("café")]
    public void ExplicitDocumentTypeMustBeASchemaIdentifier(string documentType) =>
        Assert.ThrowsAny<ArgumentException>(() => DocumentFieldSet.ForDocumentType<Plain>(documentType, p => p.Price));

    [Fact]
    public void MappedNamesCannotIntroduceFieldListSyntax()
    {
        Expression<Func<UnusableAliases, object?>>[] invalid =
            [p => p.Comma, p => p.Colon, p => p.Dot, p => p.Brackets, p => p.Space];
        foreach (var field in invalid)
            Assert.ThrowsAny<ArgumentException>(() => DocumentFieldSet.ForDocumentType<UnusableAliases>("inventory", field));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedFieldSetReachesAllThreeGetAddresses(bool withOptions)
    {
        var fieldSet = DocumentFieldSet.For<Inventory>(p => p.Price, p => p.StockCount);
        var options = new DocumentRequestOptions { Cluster = "content", TraceLevel = 3 };
        IDocumentOperations documents = _operations;
        _handler.QueueResponses(new(HttpStatusCode.NotFound), new(HttpStatusCode.NotFound), new(HttpStatusCode.NotFound));

        if (withOptions)
        {
            await documents.GetAsync<Inventory>(options, "one", fieldSet: fieldSet);
            await _operations.GetByGroupAsync<Inventory>(options, "team", "two", fieldSet: fieldSet);
            await documents.GetByNumberAsync<Inventory>(options, 42, "three", fieldSet: fieldSet);
        }
        else
        {
            await documents.GetAsync<Inventory>("one", fieldSet: fieldSet);
            await _operations.GetByGroupAsync<Inventory>("team", "two", fieldSet: fieldSet);
            await documents.GetByNumberAsync<Inventory>(42, "three", fieldSet: fieldSet);
        }

        var requests = _handler.Requests;
        string[] paths = ["/document/v1/tenant/inventory/docid/one", "/document/v1/tenant/inventory/group/team/two", "/document/v1/tenant/inventory/number/42/three"];
        Assert.Equal(paths, requests.Select(r => r.RequestUri!.AbsolutePath));
        foreach (var request in requests)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            Assert.Equal(fieldSet, query["fieldSet"]);
            Assert.Equal(withOptions ? "content" : null, query["cluster"]);
            Assert.Equal(withOptions ? "3" : null, query["tracelevel"]);
        }
    }

    [Fact]
    public async Task PositionalStringInGetMethodsBindsToTheExplicitDocumentType()
    {
        const string documentType = "other_inventory";
        IDocumentOperations documents = _operations;
        _handler.QueueResponses(new(HttpStatusCode.NotFound), new(HttpStatusCode.NotFound), new(HttpStatusCode.NotFound));

        await _operations.GetAsync<Inventory>("one", documentType);
        await documents.GetByGroupAsync<Inventory>("team", "two", documentType);
        await _operations.GetByNumberAsync<Inventory>(42, "three", documentType);

        foreach (var request in _handler.Requests)
        {
            var uri = request.RequestUri!;
            Assert.StartsWith("/document/v1/fallback/other_inventory/", uri.AbsolutePath);
            Assert.Null(HttpUtility.ParseQueryString(uri.Query)["fieldSet"]);
        }
    }

    [Fact]
    public async Task PositionalFieldSetInGetMethodsIsRejectedBeforeHttp()
    {
        var fields = DocumentFieldSet.For<Inventory>(p => p.Price);
        await Assert.ThrowsAsync<ArgumentException>(() => _operations.GetAsync<Inventory>("one", fields));
        await Assert.ThrowsAsync<ArgumentException>(() => _operations.GetByGroupAsync<Inventory>("team", "two", fields));
        await Assert.ThrowsAsync<ArgumentException>(() => _operations.GetByNumberAsync<Inventory>(42, "three", fields));
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData("[document]")]
    [InlineData("[all]")]
    [InlineData("[id]")]
    public async Task RawBuiltInsAreForwardedOnAnExplicitGet(string fieldSet)
    {
        _handler.QueueResponse(new(HttpStatusCode.NotFound));
        await _operations.GetAsync<Plain>("one", documentType: "inventory", fieldSet: fieldSet);
        var uri = Assert.Single(_handler.Requests).RequestUri!;
        Assert.Equal("/document/v1/fallback/inventory/docid/one", uri.AbsolutePath);
        Assert.Equal(fieldSet, HttpUtility.ParseQueryString(uri.Query)["fieldSet"]);
    }

    [Fact]
    public async Task VisitOptionsCarryTheSameFieldSetAcrossManualPagesAndJsonl()
    {
        var fields = DocumentFieldSet.For<Inventory>(p => p.Price, p => p.StockCount);
        var options = new VisitOptions { FieldSet = fields };
        _handler.QueueResponses(
            new(HttpStatusCode.OK) { Content = new StringContent("""{"documents":[],"continuation":"next"}""") },
            new(HttpStatusCode.OK) { Content = new StringContent("""{"documents":[]}""") },
            new(HttpStatusCode.OK) { Content = new StringContent("""{"continuation":{"percentFinished":100}}""", null, "application/jsonl") });

        var first = await _operations.VisitPageAsync<Inventory>(cluster: "content", options: options);
        Assert.Equal("next", first.Continuation);
        await _operations.VisitPageAsync<Inventory>(cluster: "content", continuation: first.Continuation, options: options);
        await foreach (var _ in _operations.VisitJsonlAsync<Inventory>(cluster: "content", options: options))
            Assert.Fail("The final continuation line should not produce a document.");

        var requests = _handler.Requests;
        Assert.Equal(3, requests.Count);
        foreach (var request in requests)
        {
            var uri = request.RequestUri!;
            Assert.Equal("/document/v1/tenant/inventory/docid/", uri.AbsolutePath);
            Assert.Equal(fields, HttpUtility.ParseQueryString(uri.Query)["fieldSet"]);
        }
        Assert.Equal("next", HttpUtility.ParseQueryString(requests[1].RequestUri!.Query)["continuation"]);
        Assert.Equal("true", HttpUtility.ParseQueryString(requests[2].RequestUri!.Query)["stream"]);
    }

    private class Plain
    {
        [VespaField(Name = "unit_price")]
        public int Price => throw new InvalidOperationException("A selector must not invoke this getter.");
    }

    [VespaDocument("inventory", Namespace = "tenant")]
    private sealed class Inventory : Plain
    {
        public int StockCount { get; init; }
        public Dimensions Details { get; init; } = new();
        [VespaId] public string? Id { get; init; }
        [JsonIgnore] public string? Ignored { get; init; }
        [JsonPropertyName("other")] public string? JsonOnlyAlias { get; init; }
    }

    private sealed class Dimensions
    {
        public int Size { get; init; }
    }

    private sealed class Aliases
    {
        [VespaField(Name = "and")] public int Lower { get; init; }
        [VespaField(Name = "And")] public int Upper { get; init; }
        [VespaField(Name = "and")] public int SameAsLower { get; init; }
    }

    private sealed class UnusableAliases
    {
        [VespaField(Name = "a,b")] public int Comma { get; init; }
        [VespaField(Name = "a:b")] public int Colon { get; init; }
        [VespaField(Name = "a.b")] public int Dot { get; init; }
        [VespaField(Name = "[all]")] public int Brackets { get; init; }
        [VespaField(Name = "a b")] public int Space { get; init; }
    }
}
