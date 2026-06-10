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

    // --- IgnoredFields header ---

    [Fact]
    public async Task PutAsync_WithIgnoredFieldsHeader_PopulatesResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        response.Headers.Add("X-Vespa-Ignored-Fields", "unknown_field, another_field");

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        var result = await ops.PutAsync("doc1", new { title = "hi" }, "music");

        Assert.NotNull(result.IgnoredFields);
        Assert.Equal(2, result.IgnoredFields!.Count);
        Assert.Contains("unknown_field", result.IgnoredFields);
        Assert.Contains("another_field", result.IgnoredFields);
    }

    [Fact]
    public async Task PutAsync_WithoutIgnoredFieldsHeader_ReturnsNull()
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        var result = await ops.PutAsync("doc1", new { title = "hi" }, "music");

        Assert.Null(result.IgnoredFields);
    }

    // --- JSONL streaming ---

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
                Content = new StringContent("")
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
                Content = new StringContent(jsonl.Trim())
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
        // Real document/v1 stream=true lines use "put", interleaved with continuation markers
        // (docs.vespa.ai/en/reference/document-v1-api-reference.html)
        var jsonl = """
        {"put":"id:test:music::1","fields":{"title":"Song A"}}
        {"continuation":{"token":"abc","percentFinished":40.0}}
        {"put":"id:test:music::2","fields":{"title":"Song B"}}
        """;

        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonl.Trim())
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
                Content = new StringContent(jsonl.Trim())
            });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        var docs = new List<VespaDocument<Dictionary<string, object>>>();
        await foreach (var doc in ops.VisitJsonlAsync<Dictionary<string, object>>("music", includeRemoves: true))
            docs.Add(doc);

        Assert.Equal(2, docs.Count);
        Assert.Equal("4", docs[1].Id);
        Assert.Null(docs[1].Fields);
    }

    [Fact]
    public async Task VisitJsonlAsync_UrlIncludesStreamParam()
    {
        string? capturedUrl = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedUrl = req.RequestUri?.ToString())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("")
            });

        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var ops = new DocumentOperations(http, _options);

        await foreach (var _ in ops.VisitJsonlAsync<object>("music")) { }

        Assert.NotNull(capturedUrl);
        Assert.Contains("stream=true", capturedUrl);
    }
}
