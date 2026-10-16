using System.Net;
using System.Text;
using Vespa.Documents;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Search;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class ApiConformanceTests : IDisposable
{
    private const string Complete = """{"continuation":{"percentFinished":100.0}}""";
    private const string Put = """{"put":"id:test:music::one","fields":{"title":"Song"}}""";
    private readonly MockHttpMessageHandler _handler = new();
    private readonly HttpClient _http;
    private readonly DocumentOperations _documents;
    private readonly SearchOperations _search;

    public ApiConformanceTests()
    {
        _http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost:8080") };
        var options = new VespaClientOptions { Endpoint = "http://localhost:8080", DefaultNamespace = "test" };
        _documents = new DocumentOperations(_http, options);
        _search = new SearchOperations(_http, options);
    }

    public void Dispose() => _http.Dispose();

    [Theory]
    [InlineData("")]
    [InlineData(Put)]
    [InlineData(Put + "\n{\"put\":")]
    [InlineData("{\"continuation\":{\"token\":\"checkpoint\",\"percentFinished\":40}}\n" + Put)]
    [InlineData("""{"continuation":{}}""")]
    [InlineData("""{"continuation":{"percentFinished":50}}""")]
    [InlineData("""{"continuation":{"token":"","percentFinished":50}}""")]
    public async Task Jsonl_IncompleteResponse_ThrowsInsteadOfCompleting(string body)
    {
        Queue(body, "application/jsonl");

        await Assert.ThrowsAsync<VespaException>(() => CollectAsync(_documents.VisitJsonlAsync<Music>("music")));
        Assert.Single(_handler.Requests);
    }

    [Theory]
    [InlineData("info")]
    [InlineData("warning")]
    public async Task Jsonl_NonErrorMessage_DoesNotInterruptVisit(string severity)
    {
        var message = $$$"""{"message":{"severity":"{{{severity}}}","text":"Visitor diagnostic"}}""";
        Queue($"{message}\n{Put}\n{{\"futureMetadata\":true}}\n{Complete}\n\n", "application/jsonl");

        var documents = await CollectAsync(_documents.VisitJsonlAsync<Music>("music"));

        Assert.Equal("Song", Assert.Single(documents).Fields!.Title);
    }

    [Fact]
    public async Task Jsonl_ErrorMessage_StillThrowsWithServerText()
    {
        Queue("""{"message":{"severity":"error","text":"Visitor failed"}}""", "application/jsonl");

        var error = await Assert.ThrowsAsync<VespaException>(() => CollectAsync(_documents.VisitJsonlAsync<Music>("music")));

        Assert.Contains("Visitor failed", error.Message);
    }

    [Theory]
    [InlineData("put", "id:test:music:g=tenant:part:one")]
    [InlineData("put", "id:test:music:n=42:part:one")]
    [InlineData("remove", "id:test:music:g=tenant:part:one")]
    [InlineData("remove", "id:test:music:n=42:part:one")]
    public async Task Jsonl_PreservesFullIdentityIncludingAddressing(string operation, string fullId)
    {
        Queue($$$"""{"{{{operation}}}":"{{{fullId}}}","fields":{"title":"Song"}}""" + "\n" + Complete, "application/jsonl");

        var document = Assert.Single(await CollectAsync(_documents.VisitJsonlAsync<Music>("music")));

        Assert.Equal(fullId, document.FullId);
        Assert.Equal("part:one", document.Id);
        if (operation == "put")
            Assert.Equal(document.Id, document.Fields!.Id);
        else
            Assert.Null(document.Fields);
    }

    [Fact]
    public async Task Jsonl_EmptyCompletedVisit_IsValid()
    {
        Queue(Complete, "application/jsonl");

        Assert.Empty(await CollectAsync(_documents.VisitJsonlAsync<Music>("music")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"documentCount\":2,\"continuation\":")]
    [InlineData("""{"documentCount":-1}""")]
    [InlineData("""{"documentCount":2,"continuation":""}""")]
    [InlineData("""{"message":"Visitor failed"}""")]
    public async Task Selection_InvalidResponse_CannotReportSuccessfulCompletion(string body)
    {
        Queue(body);

        await Assert.ThrowsAsync<VespaException>(() => _documents.DeleteBySelectionAsync("music", "music", cluster: "content"));
        Assert.Single(_handler.Requests);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("copy")]
    public async Task Selection_BodyReadFailure_PropagatesForEveryOperation(string operation)
    {
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent() });

        var error = await Assert.ThrowsAsync<VespaException>(() => operation switch
        {
            "update" => _documents.UpdateBySelectionAsync("music", new() { ["year"] = FieldOp.Increment() }, "music", cluster: "content"),
            "copy" => _documents.CopyBySelectionAsync("music", "music", "content", "archive"),
            _ => _documents.DeleteBySelectionAsync("music", "music", cluster: "content")
        });

        Assert.IsType<IOException>(error.GetBaseException());
        Assert.Contains("partially applied", error.Message);
        Assert.Single(_handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Grouping_PreservesErrorsCoverageAndTraceEvenWithoutHits(bool includeChildren)
    {
        Queue($$$"""
            {"trace":{"children":[{"message":"Backend trace"}]},"root":{
              "id":"toplevel","relevance":1,"fields":{"totalCount":2},
              "errors":[{"code":12,"summary":"Timed out","message":"Backend timed out","source":"content","transient":true}],
              "coverage":{"coverage":50,"documents":2,"full":false,"degraded":{"timeout":true}}
              {{{(includeChildren ? ",\"children\":[]" : "")}}}
            }}
            """);

        var response = await _search.GroupByAsync<Music>(new() { Yql = "select * from music where true | all(output(count()))" });

        var error = Assert.Single(response.Errors!);
        Assert.Equal(12, error.Code);
        Assert.Equal("content", error.Source);
        Assert.True(error.Transient);
        Assert.NotNull(response.Coverage);
        Assert.False(response.Coverage.Full);
        Assert.True(response.Coverage.Degraded!.Timeout);
        Assert.NotEmpty(response.Trace!.Children!);
        Assert.Equal(2, response.TotalCount);
    }

    [Fact]
    public async Task Grouping_PreservesSeparateRootAggregationsAndRootHitLists()
    {
        Queue("""
            {"root":{"id":"toplevel","relevance":1,"fields":{"totalCount":3},"children":[
              {"id":"group:root:0","relevance":1,"fields":{"count()":3,"label":"all documents"},
               "children":[{"id":"hitlist:music","children":[{"id":"id:test:music::one","relevance":1,"fields":{"title":"Song"}}]}]},
              {"id":"group:root:1","relevance":1,"fields":{"count()":9007199254740993}}
            ]}}
            """);

        var response = await _search.GroupByAsync<Music>(new() { Yql = "select * from music where true | all(output(count()))" });

        Assert.Equal(2, response.GroupingRoots.Count);
        var first = response.GroupingRoots[0];
        Assert.Equal("group:root:0", first.Id);
        Assert.Equal(3, first.Aggregations["count()"]);
        Assert.Equal("all documents", first.RawAggregations["label"].GetString());
        Assert.Equal("Song", Assert.Single(first.GetHits<Music>()).Fields!.Title);
        Assert.Equal(9007199254740993L, response.GroupingRoots[1].RawAggregations["count()"].GetInt64());
    }

    [Fact]
    public async Task Grouping_RestBucketRetainsDocumentsOutsidePredefinedRanges()
    {
        Queue("""
            {"root":{"id":"toplevel","relevance":1,"fields":{"totalCount":30},"children":[
              {"id":"group:root:0","relevance":1,"children":[
                {"id":"grouplist:price","children":[
                  {"id":"group:null","fields":{"count()":29}},
                  {"id":"group:long_bucket:0:1","fields":{"count()":1}}
                ]}
              ]}
            ]}}
            """);

        var response = await _search.GroupByAsync<Music>(new() { Yql = "select * from music where true | all(group(predefined(price, bucket[0,1))) each(output(count())))" });

        var groups = Assert.Single(response.GroupingResults).Groups;
        Assert.Equal(30, groups.Sum(g => g.Aggregations["count()"]));
        Assert.Equal("group:null", Assert.Single(groups, g => g.IsRestBucket).Id);
        Assert.Same(response.GroupingResults[0], response.GroupingRoots[0].SubGroups[0]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Search_ErrorEnvelope_PreservesOfficialErrorDetails(HttpStatusCode status)
    {
        Queue("""
            {"root":{"id":"toplevel","relevance":1,"fields":{"totalCount":0},"errors":[
              {"code":4,"summary":"Invalid query parameter","message":"Could not create query from YQL","source":"content","transient":false,"stackTrace":"Parser trace"}
            ]}}
            """, status: status);

        var error = await Assert.ThrowsAnyAsync<VespaException>(() => _search.QueryAsync<Music>("invalid YQL"));

        Assert.Equal((int)status, error.StatusCode);
        Assert.Equal(4, error.Error!.Code);
        Assert.Equal("content", error.Error.Source);
        Assert.Equal("Parser trace", error.Error.StackTrace);
        Assert.Contains("Could not create query from YQL", error.Message);
    }

    [Fact]
    public async Task Search_ErrorWithoutMessage_UsesSummary()
    {
        Queue("""{"root":{"errors":[{"code":4,"summary":"Invalid query parameter"}]}}""", status: HttpStatusCode.BadRequest);

        var error = await Assert.ThrowsAsync<VespaException>(() => _search.QueryAsync<Music>("invalid YQL"));

        Assert.Contains("Invalid query parameter", error.Message);
    }

    private void Queue(string body, string mediaType = "application/json", HttpStatusCode status = HttpStatusCode.OK) =>
        _handler.QueueResponse(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
            result.Add(item);
        return result;
    }

    private sealed record Music
    {
        [VespaId]
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
    }

    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException("Connection ended before the response body was complete.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
