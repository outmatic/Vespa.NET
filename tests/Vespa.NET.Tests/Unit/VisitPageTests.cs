using System.Net;
using System.Text.Json;
using Vespa.Documents;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class VisitPageTests : IDisposable
{
    private readonly MockHttpMessageHandler _handler = new();
    private readonly HttpClient _http;
    private readonly IDocumentOperations _documents;

    public VisitPageTests()
    {
        _http = new(_handler) { BaseAddress = new Uri("http://localhost:8080") };
        _documents = new DocumentOperations(_http, new() { DefaultNamespace = "fallback" });
    }

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task VisitPageAsync_ReturnsOnePageAndPreservesDocumentIdentity()
    {
        Queue("""
            {"pathId":"/document/v1/tenant/inventory/docid/","documentCount":1,
             "documents":[{"id":"id:tenant:inventory:g=team:part:one","fields":{"name":"First"}}],
             "continuation":"opaque+/= &?"}
            """);

        var page = await _documents.VisitPageAsync<Inventory>(selection: "inventory.name == \"First\"", cluster: "content");

        Assert.Single(_handler.Requests);
        Assert.Equal(1, page.DocumentCount);
        Assert.Equal("opaque+/= &?", page.Continuation);
        Assert.False(page.IsComplete);
        Assert.Equal("/document/v1/tenant/inventory/docid/", page.PathId);
        var document = Assert.Single(page.Documents);
        Assert.Equal("id:tenant:inventory:g=team:part:one", document.FullId);
        Assert.Equal("part:one", document.Id);
        Assert.Equal(document.Id, document.Fields!.Id);
        Assert.Equal("First", document.Fields.Name);
        Assert.Equal("/document/v1/tenant/inventory/docid/", _handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task VisitPageAsync_ResumesAfterEmptyPageWithOpaqueContinuation()
    {
        Queue("""{"documentCount":0,"documents":[],"continuation":"opaque+/= &?"}""");
        Queue("""{"documentCount":1,"documents":[{"id":"id:tenant:inventory::last","fields":{"name":"Last"}}]}""");
        var options = new VisitOptions { WantedDocumentCount = 5, Slices = 2, SliceId = 1 };
        const string selection = "inventory.name != null";

        var first = await _documents.VisitPageAsync<Inventory>(selection: selection, cluster: "content", options: options);
        Assert.Empty(first.Documents);
        Assert.False(first.IsComplete);
        Assert.Single(_handler.Requests);

        var last = await _documents.VisitPageAsync<Inventory>(selection: selection, cluster: "content", continuation: first.Continuation, options: options);

        Assert.True(last.IsComplete);
        Assert.Equal("last", Assert.Single(last.Documents).Id);
        var initialQuery = Query(_handler.Requests[0].RequestUri!);
        var resumedQuery = Query(_handler.Requests[1].RequestUri!);
        Assert.False(initialQuery.ContainsKey("continuation"));
        Assert.Equal(first.Continuation, resumedQuery["continuation"]);
        resumedQuery.Remove("continuation");
        Assert.Equal(initialQuery.OrderBy(pair => pair.Key), resumedQuery.OrderBy(pair => pair.Key));
        Assert.Equal("5", initialQuery["wantedDocumentCount"]);
        Assert.Equal("2", initialQuery["slices"]);
        Assert.Equal("1", initialQuery["sliceId"]);
        Assert.Equal(selection, initialQuery["selection"]);
    }

    [Theory]
    [InlineData("docid/", false, false)]
    [InlineData("group/team%20one", true, false)]
    [InlineData("number/42", false, true)]
    public async Task VisitPageAsync_ForwardsAddressingAndVisitOptions(string path, bool group, bool number)
    {
        Queue("""{"documentCount":0,"documents":[]}""");
        var options = new VisitOptions
        {
            Group = group ? "team one" : null,
            Number = number ? 42 : null,
            WantedDocumentCount = 7,
            Timeout = TimeSpan.FromSeconds(2),
            Concurrency = 3,
            FieldSet = "inventory:name",
            BucketSpace = "default",
            TensorFormat = "long",
            TraceLevel = 4,
            FromTimestamp = 1000,
            ToTimestamp = 2000,
            IncludeRemoves = true
        };

        var page = await _documents.VisitPageAsync<Inventory>("inventory", @namespace: "tenant", continuation: "checkpoint", options: options);

        Assert.True(page.IsComplete);
        var uri = Assert.Single(_handler.Requests).RequestUri!;
        Assert.Equal($"/document/v1/tenant/inventory/{path}", uri.AbsolutePath);
        var query = Query(uri);
        Assert.Equal("checkpoint", query["continuation"]);
        Assert.Equal("7", query["wantedDocumentCount"]);
        Assert.Equal("2000ms", query["timeout"]);
        Assert.Equal("3", query["concurrency"]);
        Assert.Equal("inventory:name", query["fieldSet"]);
        Assert.Equal("default", query["bucketSpace"]);
        Assert.Equal("long", query["format.tensors"]);
        Assert.Equal("4", query["tracelevel"]);
        Assert.Equal("1000", query["fromTimestamp"]);
        Assert.Equal("2000", query["toTimestamp"]);
        Assert.Equal("true", query["includeRemoves"]);
        Assert.False(query.ContainsKey("stream"));
    }

    [Fact]
    public async Task VisitAllPageAsync_ResumesAtRootLevel()
    {
        Queue("""{"documentCount":0,"documents":[],"continuation":"next"}""");

        var page = await _documents.VisitAllPageAsync<Inventory>("content", selection: "inventory", continuation: "saved", options: new() { BucketSpace = "global" });

        Assert.False(page.IsComplete);
        Assert.Equal("next", page.Continuation);
        var uri = Assert.Single(_handler.Requests).RequestUri!;
        Assert.Equal("/document/v1/", uri.AbsolutePath);
        var query = Query(uri);
        Assert.Equal("content", query["cluster"]);
        Assert.Equal("inventory", query["selection"]);
        Assert.Equal("saved", query["continuation"]);
        Assert.Equal("global", query["bucketSpace"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VisitPageAsync_HttpErrorsPreserveTypedExceptions(bool root)
    {
        Queue("""{"message":"Unknown cluster"}""", HttpStatusCode.NotFound);

        var error = await Assert.ThrowsAsync<VespaNotFoundException>(() => root
            ? _documents.VisitAllPageAsync<Inventory>("unknown")
            : _documents.VisitPageAsync<Inventory>("inventory", cluster: "unknown"));

        Assert.Equal("Unknown cluster", error.Message);
    }

    [Fact]
    public async Task VisitPageAsync_NullBodyDoesNotReportCompletion()
    {
        Queue("null");
        await Assert.ThrowsAsync<VespaException>(() => _documents.VisitPageAsync<Inventory>("inventory"));
    }

    [Fact]
    public async Task VisitPageAsync_RejectsMalformedJson()
    {
        Queue("{broken");
        await Assert.ThrowsAnyAsync<JsonException>(() => _documents.VisitPageAsync<Inventory>("inventory"));
    }

    [Fact]
    public async Task VisitPageAsync_PropagatesCallerCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _documents.VisitPageAsync<Inventory>("inventory", cancellationToken: source.Token));
    }

    [Fact]
    public async Task VisitPages_ValidateAddressingBeforeSending()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _documents.VisitPageAsync<Inventory>(""));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _documents.VisitAllPageAsync<Inventory>(""));
        await Assert.ThrowsAsync<ArgumentException>(() => _documents.VisitPageAsync<Inventory>("inventory", options: new() { Group = "team", Number = 42 }));
        await Assert.ThrowsAsync<ArgumentException>(() => _documents.VisitAllPageAsync<Inventory>("content", options: new() { Group = "team" }));
        await Assert.ThrowsAsync<ArgumentException>(() => _documents.VisitAllPageAsync<Inventory>("content", options: new() { Number = 42 }));
        Assert.Empty(_handler.Requests);
    }

    private void Queue(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        _handler.QueueResponse(new(status) { Content = new StringContent(body) });

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&')
        .Select(part => part.Split('=', 2))
        .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

    [VespaDocument("inventory", Namespace = "tenant")]
    public sealed class Inventory
    {
        [VespaId]
        public string? Id { get; set; }

        public string? Name { get; init; }
    }
}
