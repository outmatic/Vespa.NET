using System.Net;
using System.Text.Json;
using Vespa;
using Vespa.Documents;
using Vespa.Models;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for the Visit/Iterate API with continuation token pagination
/// </summary>
public class VisitOperationsTests : IDisposable
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly VespaClientOptions _options;
    private readonly DocumentOperations _documentOps;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public VisitOperationsTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler)
        {
            BaseAddress = new Uri("http://localhost:8080")
        };
        _options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test"
        };
        _documentOps = new DocumentOperations(_httpClient, _options);
    }

    public void Dispose() => _httpClient.Dispose();

    [Fact]
    public async Task VisitAsync_SinglePage_YieldsAllDocuments()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>(
            Documents: [
                new VespaDocument<MusicDoc> { FullId = "id:test:music::1", Fields = new MusicDoc { Title = "Song 1" } },
                new VespaDocument<MusicDoc> { FullId = "id:test:music::2", Fields = new MusicDoc { Title = "Song 2" } }
            ],
            Continuation: null,
            DocumentCount: 2,
            PathId: null));

        var results = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music"));

        Assert.Equal(2, results.Count);
        Assert.Equal("Song 1", results[0].Fields!.Title);
        Assert.Equal("Song 2", results[1].Fields!.Title);
    }

    [Fact]
    public async Task VisitAsync_MultiplePages_FollowsContinuationToken()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>(
            Documents: [new VespaDocument<MusicDoc> { FullId = "id:test:music::1", Fields = new MusicDoc { Title = "Song 1" } }],
            Continuation: "token-abc",
            DocumentCount: 1,
            PathId: null));

        EnqueuePage(new VespaVisitResponse<MusicDoc>(
            Documents: [new VespaDocument<MusicDoc> { FullId = "id:test:music::2", Fields = new MusicDoc { Title = "Song 2" } }],
            Continuation: null,
            DocumentCount: 1,
            PathId: null));

        var results = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music"));

        Assert.Equal(2, results.Count);
        Assert.Equal("Song 1", results[0].Fields!.Title);
        Assert.Equal("Song 2", results[1].Fields!.Title);
    }

    [Fact]
    public async Task VisitAsync_MultiplePages_SendsTwoRequests()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], "token-1", 0, null));
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music"));

        Assert.Equal(2, _mockHandler.Requests.Count);
    }

    [Fact]
    public async Task VisitAsync_EmptyPage_YieldsNothing()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        var results = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music"));

        Assert.Empty(results);
    }

    [Fact]
    public async Task VisitAsync_WithSelection_AddsQueryParam()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music", selection: "music.year > 2000"));

        Assert.Contains("selection=", _mockHandler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task VisitAsync_WithCluster_AddsQueryParam()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music", cluster: "mycluster"));

        Assert.Contains("cluster=mycluster", _mockHandler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task VisitAsync_WithWantedDocumentCount_AddsQueryParam()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music", options: new VisitOptions { WantedDocumentCount = 100 }));

        Assert.Contains("wantedDocumentCount=100", _mockHandler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task VisitAsync_WithTimeout_AddsQueryParam()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music", options: new VisitOptions { Timeout = TimeSpan.FromSeconds(30) }));

        Assert.Contains("timeout=30000ms", _mockHandler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task VisitAsync_WithFieldSet_AddsQueryParam()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music", options: new VisitOptions { FieldSet = "music:[document]" }));

        Assert.Contains("fieldSet=", _mockHandler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task VisitAsync_WithCustomNamespace_UsesCustomNamespace()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music", @namespace: "custom"));

        Assert.StartsWith("/document/v1/custom/", _mockHandler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task VisitAsync_HttpError_ThrowsVespaException()
    {
        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{}")
        });

        var ex = await Assert.ThrowsAnyAsync<VespaException>(async () =>
            _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music")));
        Assert.IsType<VespaServerException>(ex);
    }

    [Fact]
    public async Task VisitAsync_SecondPageIncludesContinuationParam()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], "my-token", 0, null));
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music"));

        // First request should NOT have continuation param
        Assert.DoesNotContain("continuation=", _mockHandler.Requests[0].RequestUri!.Query);
        // Second request SHOULD have the continuation token
        Assert.Contains("continuation=my-token", _mockHandler.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public async Task VisitAsync_ThreePages_AccumulatesAllDocuments()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>(
            [new() { Id = "1", Fields = new MusicDoc { Title = "Song 1" } }], "t1", 1, null));
        EnqueuePage(new VespaVisitResponse<MusicDoc>(
            [new() { Id = "2", Fields = new MusicDoc { Title = "Song 2" } },
             new() { Id = "3", Fields = new MusicDoc { Title = "Song 3" } }], "t2", 2, null));
        EnqueuePage(new VespaVisitResponse<MusicDoc>(
            [new() { Id = "4", Fields = new MusicDoc { Title = "Song 4" } }], null, 1, null));

        var results = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music"));

        Assert.Equal(4, results.Count);
    }

    // --- M9: New visit parameters ---

    [Fact]
    public async Task VisitAsync_WithSlices_AddsQueryParam()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music", options: new VisitOptions { Slices = 4 }));

        Assert.Contains("slices=4", _mockHandler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task VisitAsync_WithSliceId_AddsQueryParam()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music", options: new VisitOptions { Slices = 4, SliceId = 2 }));

        var query = _mockHandler.Requests[0].RequestUri!.Query;
        Assert.Contains("slices=4", query);
        Assert.Contains("sliceId=2", query);
    }

    [Fact]
    public async Task VisitAsync_WithConcurrency_AddsQueryParam()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music", options: new VisitOptions { Concurrency = 8 }));

        Assert.Contains("concurrency=8", _mockHandler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task VisitAsync_WithFromTimestamp_AddsQueryParam()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music", options: new VisitOptions { FromTimestamp = 1000000 }));

        Assert.Contains("fromTimestamp=1000000", _mockHandler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task VisitAsync_WithToTimestamp_AddsQueryParam()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music", options: new VisitOptions { ToTimestamp = 2000000 }));

        Assert.Contains("toTimestamp=2000000", _mockHandler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task VisitAsync_WithIncludeRemoves_AddsQueryParam()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music", options: new VisitOptions { IncludeRemoves = true }));

        Assert.Contains("includeRemoves=true", _mockHandler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task VisitAsync_WithBucketSpace_AddsQueryParam()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music", options: new VisitOptions { BucketSpace = "global" }));

        Assert.Contains("bucketSpace=global", _mockHandler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task VisitAsync_AllNewParams_AddsAllQueryParams()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music", options: new VisitOptions
        {
            Slices = 4,
            SliceId = 1,
            Concurrency = 8,
            FromTimestamp = 1000,
            ToTimestamp = 2000,
            IncludeRemoves = true,
            BucketSpace = "default"
        }));

        var query = _mockHandler.Requests[0].RequestUri!.Query;
        Assert.Contains("slices=4", query);
        Assert.Contains("sliceId=1", query);
        Assert.Contains("concurrency=8", query);
        Assert.Contains("fromTimestamp=1000", query);
        Assert.Contains("toTimestamp=2000", query);
        Assert.Contains("includeRemoves=true", query);
        Assert.Contains("bucketSpace=default", query);
    }

    // --- Group/number/root visit levels ---

    [Fact]
    public async Task VisitAsync_WithGroupOption_UsesGroupPathLevel()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music",
            options: new VisitOptions { Group = "my group" }));

        Assert.Equal("/document/v1/test/music/group/my%20group",
            _mockHandler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task VisitAsync_WithNumberOption_UsesNumberPathLevel()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music",
            options: new VisitOptions { Number = 42L }));

        Assert.Equal("/document/v1/test/music/number/42",
            _mockHandler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task VisitAsync_WithGroupAndNumber_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music",
                options: new VisitOptions { Group = "g", Number = 42L })));
    }

    [Fact]
    public async Task VisitAllAsync_UsesRootPathAndClusterParam()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAllAsync<MusicDoc>("mycluster"));

        Assert.Equal("/document/v1/", _mockHandler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("cluster=mycluster", _mockHandler.Requests[0].RequestUri!.Query);
    }

    [Fact]
    public async Task VisitAllAsync_FollowsContinuationTokens()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>(
            [new() { Id = "1", Fields = new MusicDoc { Title = "Song 1" } }], "t1", 1, null));
        EnqueuePage(new VespaVisitResponse<MusicDoc>(
            [new() { Id = "2", Fields = new MusicDoc { Title = "Song 2" } }], null, 1, null));

        var results = await CollectAsync(_documentOps.VisitAllAsync<MusicDoc>("mycluster"));

        Assert.Equal(2, results.Count);
        Assert.Contains("continuation=t1", _mockHandler.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public async Task VisitAllAsync_EmptyCluster_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            _ = await CollectAsync(_documentOps.VisitAllAsync<MusicDoc>("")));
    }

    [Fact]
    public async Task VisitAllAsync_WithGroupOption_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            _ = await CollectAsync(_documentOps.VisitAllAsync<MusicDoc>("mycluster",
                options: new VisitOptions { Group = "g" })));
    }

    // --- Error mapping: visit errors surface the server message ---

    [Fact]
    public async Task VisitAsync_HttpError_SurfacesServerMessage()
    {
        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"pathId":"/document/v1/","message":"No content cluster 'x'"}""")
        });

        var ex = await Assert.ThrowsAnyAsync<VespaException>(async () =>
            _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music")));
        Assert.Contains("No content cluster 'x'", ex.Message);
    }

    // --- dryRun is rejected by Vespa on visits — never emitted ---

    [Fact]
    public async Task VisitAsync_NeverEmitsDryRun()
    {
        EnqueuePage(new VespaVisitResponse<MusicDoc>([], null, 0, null));

        _ = await CollectAsync(_documentOps.VisitAsync<MusicDoc>("music",
            options: new VisitOptions { TraceLevel = 1 }));

        Assert.DoesNotContain("dryRun", _mockHandler.Requests[0].RequestUri!.Query);
        Assert.Contains("tracelevel=1", _mockHandler.Requests[0].RequestUri!.Query);
    }

    // --- Helpers ---

    private void EnqueuePage<T>(VespaVisitResponse<T> page) where T : class
    {
        var json = JsonSerializer.Serialize(page, JsonOpts);
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

    private class MusicDoc
    {
        public string Title { get; set; } = string.Empty;
    }
}
