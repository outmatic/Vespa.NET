using System.Net;
using Moq;
using Moq.Protected;
using Vespa;
using Vespa.Documents;
using Vespa.Models;
using Xunit;

namespace Vespa.Tests.Unit;

public class DocumentRequestOptionsTests
{
    private readonly Mock<HttpMessageHandler> _mockHandler = new();
    private readonly VespaClientOptions _options = new()
    {
        Endpoint = "http://localhost:8080",
        DefaultNamespace = "test"
    };

    // --- DocumentRequestOptions ---

    [Fact]
    public void ToQueryParams_Route_EmitsRoute()
    {
        var opts = new DocumentRequestOptions { Route = "default/chain.indexing" };
        var ps = opts.ToQueryParams().ToList();
        Assert.Contains(("route", "default/chain.indexing"), ps);
    }

    [Fact]
    public void ToQueryParams_TraceLevel_EmitsTracelevel()
    {
        var opts = new DocumentRequestOptions { TraceLevel = 5 };
        var ps = opts.ToQueryParams().ToList();
        Assert.Contains(("tracelevel", "5"), ps);
    }

    [Fact]
    public void ToQueryParams_TensorFormat_EmitsFormatTensors()
    {
        var opts = new DocumentRequestOptions { TensorFormat = "short" };
        var ps = opts.ToQueryParams().ToList();
        Assert.Contains(("format.tensors", "short"), ps);
    }

    [Fact]
    public void ToQueryParams_DryRun_EmitsDryRun()
    {
        var opts = new DocumentRequestOptions { DryRun = true };
        var ps = opts.ToQueryParams().ToList();
        Assert.Contains(("dryRun", "true"), ps);
    }

    [Fact]
    public void ToQueryParams_Timeout_EmitsMilliseconds()
    {
        var opts = new DocumentRequestOptions { Timeout = TimeSpan.FromMilliseconds(2500) };
        var ps = opts.ToQueryParams().ToList();
        Assert.Contains(("timeout", "2500ms"), ps);
    }

    [Fact]
    public async Task PutAsync_WithTimeoutOption_IncludesTimeoutInUrl()
    {
        string? capturedUrl = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUrl = req.RequestUri?.ToString())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        await ops.PutAsync("doc1", new { title = "hi" }, "music",
            requestOptions: new DocumentRequestOptions { Timeout = TimeSpan.FromSeconds(3) });

        Assert.NotNull(capturedUrl);
        Assert.Contains("timeout=3000ms", capturedUrl);
    }

    [Fact]
    public void ToQueryParams_DefaultOptions_EmitsNothing()
    {
        var opts = new DocumentRequestOptions();
        Assert.Empty(opts.ToQueryParams());
    }

    // --- PutAsync with RequestOptions ---

    [Fact]
    public async Task PutAsync_WithRequestOptions_IncludesParams()
    {
        string? capturedUrl = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUrl = req.RequestUri?.ToString())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        await ops.PutAsync("doc1", new { title = "hi" }, "music", requestOptions: new DocumentRequestOptions
        {
            Route = "indexing",
            TraceLevel = 3,
            TensorFormat = "long"
        });

        Assert.NotNull(capturedUrl);
        Assert.Contains("route=indexing", capturedUrl);
        Assert.Contains("tracelevel=3", capturedUrl);
        Assert.Contains("format.tensors=long", capturedUrl);
    }

    // --- Cluster on GET ---

    [Fact]
    public async Task GetAsync_WithClusterOption_ForwardsClusterParam()
    {
        string? capturedUrl = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUrl = req.RequestUri?.ToString())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"id:test:music::doc1","fields":{}}""")
            });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        await ops.GetAsync<object>("doc1", "music",
            requestOptions: new DocumentRequestOptions { Cluster = "content" });

        Assert.NotNull(capturedUrl);
        Assert.Contains("cluster=content", capturedUrl);
    }

    [Fact]
    public async Task PutAsync_WithClusterOption_DoesNotForwardCluster()
    {
        string? capturedUrl = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUrl = req.RequestUri?.ToString())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        await ops.PutAsync("doc1", new { title = "hi" }, "music",
            requestOptions: new DocumentRequestOptions { Cluster = "content" });

        Assert.NotNull(capturedUrl);
        Assert.DoesNotContain("cluster=", capturedUrl);
    }

    [Fact]
    public void ToQueryParams_Cluster_IsNotEmitted()
    {
        var opts = new DocumentRequestOptions { Cluster = "content" };
        Assert.Empty(opts.ToQueryParams());
    }

    // --- X-Vespa-Ignored-Fields flag header ---

    [Fact]
    public async Task PutAsync_WithIgnoredFieldsHeader_SetsHadIgnoredFields()
    {
        // Vespa sets the header to the flag value "true" — it never lists field names.
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        response.Headers.Add("X-Vespa-Ignored-Fields", "true");

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        var result = await ops.PutAsync("doc1", new { title = "hi" }, "music");

        Assert.True(result.HadIgnoredFields);
    }

    [Fact]
    public async Task PutAsync_WithoutIgnoredFieldsHeader_HadIgnoredFieldsIsFalse()
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        var result = await ops.PutAsync("doc1", new { title = "hi" }, "music");

        Assert.False(result.HadIgnoredFields);
    }

    // --- JSONL streaming ---

    private static StringContent JsonlContent(string jsonl) =>
        new(jsonl, System.Text.Encoding.UTF8, "application/jsonl");

    [Fact]
    public async Task VisitJsonlAsync_SendsJsonlAcceptHeader()
    {
        string? capturedAccept = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
                capturedAccept = req.Headers.Accept.FirstOrDefault()?.MediaType)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonlContent("")
            });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        await foreach (var _ in ops.VisitJsonlAsync<object>("music")) { }

        Assert.Equal("application/jsonl", capturedAccept);
    }

    [Fact]
    public async Task VisitJsonlAsync_ParsesJsonlLines()
    {
        var jsonl = """
        {"id":"id:test:music::1","fields":{"title":"Song A"}}
        {"id":"id:test:music::2","fields":{"title":"Song B"}}
        """;

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonlContent(jsonl.Trim())
            });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        var docs = new List<VespaDocument<Dictionary<string, object>>>();
        await foreach (var doc in ops.VisitJsonlAsync<Dictionary<string, object>>("music"))
            docs.Add(doc);

        Assert.Equal(2, docs.Count);
    }

    [Fact]
    public async Task VisitJsonlAsync_ParsesRealStreamFormat()
    {
        // Real document/v1 stream=true lines use "put", interleaved with continuation
        // markers; the final continuation has no token iff the visit completed
        // (docs.vespa.ai/en/reference/document-v1-api-reference.html)
        var jsonl = """
        {"put":"id:test:music::1","fields":{"title":"Song A"}}
        {"continuation":{"token":"abc","percentFinished":40.0}}
        {"put":"id:test:music::2","fields":{"title":"Song B"}}
        {"continuation":{"percentFinished":100.0}}
        """;

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonlContent(jsonl.Trim())
            });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        var docs = new List<VespaDocument<Dictionary<string, object>>>();
        await foreach (var doc in ops.VisitJsonlAsync<Dictionary<string, object>>("music"))
            docs.Add(doc);

        Assert.Equal(2, docs.Count);
        Assert.Equal("1", docs[0].Id);
        Assert.Equal("2", docs[1].Id);
        Assert.NotNull(docs[0].Fields);
    }

    [Fact]
    public async Task VisitJsonlAsync_TrailingContinuationToken_ResumesAutomatically()
    {
        // A visit interrupted server-side (e.g. timeout) ends the stream with a
        // continuation that still carries a token — the client must resume from it.
        var handler = new Vespa.Tests.Helpers.MockHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonlContent("""
                {"put":"id:test:music::1","fields":{"title":"Song A"}}
                {"continuation":{"token":"resume-me","percentFinished":40.0}}
                """)
        });
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonlContent("""
                {"put":"id:test:music::2","fields":{"title":"Song B"}}
                {"continuation":{"percentFinished":100.0}}
                """)
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        var docs = new List<VespaDocument<Dictionary<string, object>>>();
        await foreach (var doc in ops.VisitJsonlAsync<Dictionary<string, object>>("music"))
            docs.Add(doc);

        Assert.Equal(["1", "2"], docs.Select(d => d.Id));
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("continuation=", handler.Requests[0].RequestUri!.Query);
        Assert.Contains("continuation=resume-me", handler.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public async Task VisitJsonlAsync_MessageLine_ThrowsVespaException()
    {
        var jsonl = """
        {"put":"id:test:music::1","fields":{"title":"Song A"}}
        {"message":"the visit failed hard"}
        """;

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonlContent(jsonl.Trim())
            });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        var ex = await Assert.ThrowsAsync<VespaException>(async () =>
        {
            await foreach (var _ in ops.VisitJsonlAsync<Dictionary<string, object>>("music")) { }
        });
        Assert.Contains("the visit failed hard", ex.Message);
    }

    [Fact]
    public async Task VisitJsonlAsync_NonJsonlContentType_ThrowsVespaException()
    {
        // Vespa < 8.593 answers with a plain JSON body — silently yielding zero
        // documents would look like an empty corpus.
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"documents":[],"documentCount":0}""",
                    System.Text.Encoding.UTF8, "application/json")
            });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        var ex = await Assert.ThrowsAsync<VespaException>(async () =>
        {
            await foreach (var _ in ops.VisitJsonlAsync<Dictionary<string, object>>("music")) { }
        });
        Assert.Contains("application/jsonl", ex.Message);
    }

    [Fact]
    public async Task VisitJsonlAsync_WithIncludeRemoves_YieldsTombstones()
    {
        var jsonl = """
        {"put":"id:test:music::1","fields":{"title":"Song A"}}
        {"remove":"id:test:music::4"}
        """;

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonlContent(jsonl.Trim())
            });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        var docs = new List<VespaDocument<Dictionary<string, object>>>();
        await foreach (var doc in ops.VisitJsonlAsync<Dictionary<string, object>>("music",
            options: new VisitOptions { IncludeRemoves = true }))
            docs.Add(doc);

        Assert.Equal(2, docs.Count);
        Assert.Equal("4", docs[1].Id);
        Assert.Null(docs[1].Fields);
    }

    [Fact]
    public async Task VisitJsonlAsync_MalformedLine_IsSkipped()
    {
        // A truncated line (connection drop mid-line) must not abort the whole stream
        var jsonl = """
        {"put":"id:test:music::1","fields":{"title":"Song A"}}
        {"put":"id:test:music::2","fie
        {"put":"id:test:music::3","fields":{"title":"Song C"}}
        """;

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonlContent(jsonl.Trim())
            });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        var docs = new List<VespaDocument<Dictionary<string, object>>>();
        await foreach (var doc in ops.VisitJsonlAsync<Dictionary<string, object>>("music"))
            docs.Add(doc);

        Assert.Equal(2, docs.Count);
        Assert.Equal(["1", "3"], docs.Select(d => d.Id));
    }

    [Fact]
    public async Task VisitJsonlAsync_UrlIncludesStreamParam_ButNeverWantedDocumentCount()
    {
        string? capturedUrl = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUrl = req.RequestUri?.ToString())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonlContent("")
            });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        // wantedDocumentCount is redundant with stream=true and therefore omitted.
        await foreach (var _ in ops.VisitJsonlAsync<object>("music",
            options: new VisitOptions { WantedDocumentCount = 100 })) { }

        Assert.NotNull(capturedUrl);
        Assert.Contains("stream=true", capturedUrl);
        Assert.DoesNotContain("wantedDocumentCount", capturedUrl);
    }
}
