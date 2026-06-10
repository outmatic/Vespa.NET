using System.Net;
using System.Text.Json;
using Vespa;
using Vespa.Models;
using Vespa.Search;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

public class SearchPagedAsyncTests : IDisposable
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly SearchOperations _searchOps;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public SearchPagedAsyncTests()
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

    [Fact]
    public async Task SearchPagedAsync_SinglePartialPage_YieldsOnePage()
    {
        EnqueuePage(["a", "b"]);    // 2 hits, pageSize=10 → partial → stop

        var pages = await CollectAsync(
            _searchOps.SearchPagedAsync<Doc>(new VespaSearchRequest { Yql = "select * from docs;" }, pageSize: 10));

        Assert.Single(pages);
        Assert.Equal(2, pages[0].Root.Children.Count);
        Assert.Single(_mockHandler.Requests);
    }

    [Fact]
    public async Task SearchPagedAsync_TwoFullPagesAndPartial_YieldsThreePages()
    {
        EnqueuePage(["a", "b"]);   // full (pageSize=2)
        EnqueuePage(["c", "d"]);   // full
        EnqueuePage(["e"]);         // partial → stop

        var pages = await CollectAsync(
            _searchOps.SearchPagedAsync<Doc>(new VespaSearchRequest { Yql = "select * from docs;" }, pageSize: 2));

        Assert.Equal(3, pages.Count);
        Assert.Equal(2, pages[0].Root.Children.Count);
        Assert.Equal(2, pages[1].Root.Children.Count);
        Assert.Single(pages[2].Root.Children);
        Assert.Equal(3, _mockHandler.Requests.Count);
    }

    [Fact]
    public async Task SearchPagedAsync_EmptyResults_YieldsOneEmptyPage()
    {
        EnqueuePage([]);    // 0 hits → yield empty page, then stop

        var pages = await CollectAsync(
            _searchOps.SearchPagedAsync<Doc>(new VespaSearchRequest { Yql = "select * from docs;" }, pageSize: 10));

        Assert.Single(pages);
        Assert.Empty(pages[0].Root.Children);
        Assert.Single(_mockHandler.Requests);
    }

    [Fact]
    public async Task SearchPagedAsync_DoesNotMutateCallerRequest()
    {
        EnqueuePage(["a", "b"]);   // full (pageSize=2)
        EnqueuePage(["c"]);         // partial → stop

        var request = new VespaSearchRequest { Yql = "select * from docs;", Hits = 7, Offset = 0 };
        _ = await CollectAsync(_searchOps.SearchPagedAsync<Doc>(request, pageSize: 2));

        // Re-enumerating (or reusing) the same request must start from the caller's state
        Assert.Equal(7, request.Hits);
        Assert.Equal(0, request.Offset);
    }

    [Fact]
    public async Task SearchPagedAsync_OffsetIncrementsCorrectly()
    {
        EnqueuePage(["a", "b"]);   // pageSize=2, full
        EnqueuePage(["c"]);         // partial → stop

        _ = await CollectAsync(
            _searchOps.SearchPagedAsync<Doc>(new VespaSearchRequest { Yql = "select * from docs;" }, pageSize: 2));

        var firstBody = await ReadBodyAsync(_mockHandler.Requests[0]);
        var secondBody = await ReadBodyAsync(_mockHandler.Requests[1]);

        Assert.Contains(@"""offset"":0", firstBody);
        Assert.Contains(@"""offset"":2", secondBody);
    }

    [Fact]
    public async Task SearchPagedAsync_RespectsInitialOffset()
    {
        EnqueuePage(["a"]);

        _ = await CollectAsync(
            _searchOps.SearchPagedAsync<Doc>(
                new VespaSearchRequest { Yql = "select * from docs;", Offset = 50 },
                pageSize: 10));

        var body = await ReadBodyAsync(_mockHandler.Requests[0]);
        Assert.Contains(@"""offset"":50", body);
    }

    [Fact]
    public async Task SearchPagedAsync_InvalidPageSize_ThrowsArgumentOutOfRangeException()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            _ = await CollectAsync(
                _searchOps.SearchPagedAsync<Doc>(
                    new VespaSearchRequest { Yql = "select * from docs;" }, pageSize: 0)));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
