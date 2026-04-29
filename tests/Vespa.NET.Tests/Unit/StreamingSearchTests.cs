using System.Net;
using System.Text.Json;
using Vespa;
using Vespa.Models;
using Vespa.Search;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for streaming search: Vespa streaming mode parameters and SearchStreamAsync pagination
/// </summary>
public class StreamingSearchTests : IDisposable
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly SearchOperations _searchOps;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public StreamingSearchTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler) { BaseAddress = new Uri("http://localhost:8080") };
        _searchOps = new SearchOperations(_httpClient, new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test"
        });
    }

    public void Dispose() => _httpClient.Dispose();

    // ── Vespa streaming search parameters ─────────────────────────────────────

    [Fact]
    public void VespaSearchRequest_StreamingUserId_SerializesWithDotKey()
    {
        var request = new VespaSearchRequest
        {
            Yql = "select * from email;",
            StreamingUserId = "alice@example.com"
        };
        var json = JsonSerializer.Serialize(request, JsonOpts);
        Assert.Contains(@"""streaming.userid""", json);
        Assert.Contains("alice@example.com", json);
    }

    [Fact]
    public void VespaSearchRequest_StreamingGroupName_SerializesWithDotKey()
    {
        var request = new VespaSearchRequest
        {
            Yql = "select * from docs;",
            StreamingGroupName = "teamA"
        };
        var json = JsonSerializer.Serialize(request, JsonOpts);
        Assert.Contains(@"""streaming.groupname""", json);
        Assert.Contains("teamA", json);
    }

    [Fact]
    public void VespaSearchRequest_StreamingSelection_SerializesWithDotKey()
    {
        var request = new VespaSearchRequest
        {
            Yql = "select * from docs;",
            StreamingSelection = "doc.category == 'public'"
        };
        var json = JsonSerializer.Serialize(request, JsonOpts);
        Assert.Contains(@"""streaming.selection""", json);
    }

    [Fact]
    public void VespaSearchRequest_StreamingMaxBuckets_SerializesWithDotKey()
    {
        var request = new VespaSearchRequest
        {
            Yql = "select * from docs;",
            StreamingMaxBucketsPerVisit = 5
        };
        var json = JsonSerializer.Serialize(request, JsonOpts);
        Assert.Contains(@"""streaming.maxbucketspervisitor""", json);
        Assert.Contains("5", json);
    }

    [Fact]
    public void VespaSearchRequest_NoStreamingParams_FieldsAbsent()
    {
        var request = new VespaSearchRequest { Yql = "select * from docs;" };
        var json = JsonSerializer.Serialize(request, JsonOpts);
        Assert.DoesNotContain("streaming.", json);
    }

    // ── SearchStreamAsync: basic pagination ───────────────────────────────────

    [Fact]
    public async Task SearchStreamAsync_SinglePage_YieldsAllHits()
    {
        EnqueuePage(["id1", "id2", "id3"]);

        var hits = await CollectAsync(
            _searchOps.SearchStreamAsync<Doc>(new VespaSearchRequest { Yql = "select * from docs;" }, pageSize: 10));

        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public async Task SearchStreamAsync_TwoFullPages_FetchesBothPages()
    {
        EnqueuePage(["a", "b", "c"]);   // full page (pageSize=3)
        EnqueuePage(["d", "e", "f"]);   // full page
        EnqueuePage([]);                 // empty → stop

        var hits = await CollectAsync(
            _searchOps.SearchStreamAsync<Doc>(new VespaSearchRequest { Yql = "select * from docs;" }, pageSize: 3));

        Assert.Equal(6, hits.Count);
        Assert.Equal(3, _mockHandler.Requests.Count);
    }

    [Fact]
    public async Task SearchStreamAsync_PartialLastPage_StopsWithoutExtraRequest()
    {
        EnqueuePage(["a", "b", "c"]);   // full page (pageSize=3)
        EnqueuePage(["d", "e"]);         // partial → last page, no more requests

        var hits = await CollectAsync(
            _searchOps.SearchStreamAsync<Doc>(new VespaSearchRequest { Yql = "select * from docs;" }, pageSize: 3));

        Assert.Equal(5, hits.Count);
        Assert.Equal(2, _mockHandler.Requests.Count); // only 2 HTTP calls
    }

    [Fact]
    public async Task SearchStreamAsync_EmptyResults_YieldsNothing()
    {
        EnqueuePage([]);

        var hits = await CollectAsync(
            _searchOps.SearchStreamAsync<Doc>(new VespaSearchRequest { Yql = "select * from docs;" }, pageSize: 10));

        Assert.Empty(hits);
        Assert.Single(_mockHandler.Requests);
    }

    [Fact]
    public async Task SearchStreamAsync_SecondPage_UsesCorrectOffset()
    {
        EnqueuePage(["a", "b", "c"]);   // pageSize=3
        EnqueuePage([]);

        _ = await CollectAsync(
            _searchOps.SearchStreamAsync<Doc>(new VespaSearchRequest { Yql = "select * from docs;" }, pageSize: 3));

        // First request: offset=0
        var firstBody = await ReadBodyAsync(_mockHandler.Requests[0]);
        Assert.Contains(@"""offset"":0", firstBody);

        // Second request: offset=3
        var secondBody = await ReadBodyAsync(_mockHandler.Requests[1]);
        Assert.Contains(@"""offset"":3", secondBody);
    }

    [Fact]
    public async Task SearchStreamAsync_RespectsInitialOffset()
    {
        EnqueuePage(["a"]);

        _ = await CollectAsync(
            _searchOps.SearchStreamAsync<Doc>(
                new VespaSearchRequest { Yql = "select * from docs;", Offset = 50 },
                pageSize: 10));

        var body = await ReadBodyAsync(_mockHandler.Requests[0]);
        Assert.Contains(@"""offset"":50", body);
    }

    [Fact]
    public async Task SearchStreamAsync_PageSizeSetInRequest()
    {
        EnqueuePage(["a"]);

        _ = await CollectAsync(
            _searchOps.SearchStreamAsync<Doc>(new VespaSearchRequest { Yql = "select * from docs;" }, pageSize: 25));

        var body = await ReadBodyAsync(_mockHandler.Requests[0]);
        Assert.Contains(@"""hits"":25", body);
    }

    [Fact]
    public async Task SearchStreamAsync_PropagatesRankingConfig()
    {
        EnqueuePage(["a"]);

        _ = await CollectAsync(
            _searchOps.SearchStreamAsync<Doc>(
                new VespaSearchRequest
                {
                    Yql = "select * from docs;",
                    Ranking = new RankingConfig { Profile = "semantic" }
                }, pageSize: 10));

        var body = await ReadBodyAsync(_mockHandler.Requests[0]);
        Assert.Contains("semantic", body);
    }

    [Fact]
    public async Task SearchStreamAsync_PropagatesStreamingUserId()
    {
        EnqueuePage(["a"]);

        _ = await CollectAsync(
            _searchOps.SearchStreamAsync<Doc>(
                new VespaSearchRequest
                {
                    Yql = "select * from email;",
                    StreamingUserId = "bob@example.com"
                }, pageSize: 10));

        var body = await ReadBodyAsync(_mockHandler.Requests[0]);
        Assert.Contains("bob@example.com", body);
    }

    [Fact]
    public async Task SearchStreamAsync_HttpError_ThrowsVespaException()
    {
        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("error")
        });

        var ex = await Assert.ThrowsAnyAsync<VespaException>(async () =>
            _ = await CollectAsync(
                _searchOps.SearchStreamAsync<Doc>(
                    new VespaSearchRequest { Yql = "select * from docs;" })));
        Assert.IsType<VespaServerException>(ex);
    }

    [Fact]
    public async Task SearchStreamAsync_InvalidPageSize_ThrowsArgumentOutOfRangeException()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            _ = await CollectAsync(
                _searchOps.SearchStreamAsync<Doc>(
                    new VespaSearchRequest { Yql = "select * from docs;" }, pageSize: 0)));
    }

    [Fact]
    public async Task SearchStreamAsync_ThreeFullPagesOnePartial_CorrectHitCount()
    {
        EnqueuePage(["a", "b"]);   // pageSize=2, full
        EnqueuePage(["c", "d"]);   // full
        EnqueuePage(["e", "f"]);   // full
        EnqueuePage(["g"]);         // partial → stop

        var hits = await CollectAsync(
            _searchOps.SearchStreamAsync<Doc>(new VespaSearchRequest { Yql = "select * from docs;" }, pageSize: 2));

        Assert.Equal(7, hits.Count);
        Assert.Equal(4, _mockHandler.Requests.Count);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void EnqueuePage(IEnumerable<string> ids)
    {
        var children = ids.Select(id => new
        {
            id,
            relevance = 1.0,
            source = "content",
            fields = new { title = id }
        }).ToArray();

        var response = new
        {
            root = new
            {
                id = "toplevel",
                relevance = 1.0,
                fields = new { totalCount = 100 },
                children
            }
        };

        var json = JsonSerializer.Serialize(response, JsonOpts);
        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
            list.Add(item);
        return list;
    }

    private static async Task<string> ReadBodyAsync(HttpRequestMessage req) =>
        req.Content is null ? "" : await req.Content.ReadAsStringAsync();

    private class Doc
    {
        public string Title { get; set; } = string.Empty;
    }
}
