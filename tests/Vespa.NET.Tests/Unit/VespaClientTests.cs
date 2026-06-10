using System.Net;
using System.Reflection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using Moq.Protected;
using Vespa;
using Vespa.Admin;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for VespaClient covering initialization, health checks, and metrics
/// </summary>
public class VespaClientTests : IDisposable
{
    private readonly Mock<HttpMessageHandler> _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly VespaClientOptions _options;

    public VespaClientTests()
    {
        _mockHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHandler.Object)
        {
            BaseAddress = new Uri("http://localhost:8080")
        };
        _options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test"
        };
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    #region Constructor Tests (4 tests)

    [Fact]
    public void Constructor_WithValidParameters_InitializesCorrectly()
    {
        // Act
        var client = new VespaClient(_httpClient, _options);

        // Assert
        Assert.NotNull(client);
        Assert.NotNull(client.Documents);
        Assert.NotNull(client.Search);
        Assert.NotNull(client.Feed);
        Assert.NotNull(client.Admin);
    }

    [Fact]
    public void Constructor_DoesNotBuildAdminClient_UntilAdminIsAccessed()
    {
        // Building the admin HttpClient loads the mTLS certificate from disk;
        // with a missing file that throws — so constructing the client without
        // touching Admin proves the config-server client is created lazily.
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test",
            CertificatePath = "/nonexistent/data-plane-cert.pem",
            ClientKeyPath = "/nonexistent/data-plane-key.pem"
        };

        using var client = new VespaClient(_httpClient, options);

        Assert.ThrowsAny<Exception>(() => client.Admin);
    }

    [Fact]
    public void Admin_AfterDispose_ThrowsObjectDisposedException()
    {
        // Materializing the lazy admin client after Dispose would resurrect an
        // HttpClient + handler nobody disposes
        var client = new VespaClient(_httpClient, _options);
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.Admin);
    }

    [Fact]
    public void Constructor_WithNullHttpClient_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new VespaClient(null!, _options));
    }

    [Fact]
    public void Constructor_WithNullOptions_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new VespaClient(_httpClient, null!));
    }

    [Fact]
    public void Constructor_WithInvalidOptions_ThrowsArgumentException()
    {
        // Arrange
        var invalidOptions = new VespaClientOptions
        {
            Endpoint = "", // Invalid empty endpoint
            DefaultNamespace = "test"
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            new VespaClient(_httpClient, invalidOptions));
    }

    #endregion

    #region HealthCheckAsync Tests (3 tests)

    [Fact]
    public async Task HealthCheckAsync_WhenHealthy_ReturnsTrue()
    {
        // Arrange
        SetupSuccessResponse();
        var client = new VespaClient(_httpClient, _options);

        // Act
        var result = await client.HealthCheckAsync();

        // Assert
        Assert.True(result);
        VerifyHttpCall(HttpMethod.Get, "/state/v1/health");
    }

    [Fact]
    public async Task HealthCheckAsync_WhenUnhealthy_ReturnsFalse()
    {
        // Arrange
        SetupErrorResponse(HttpStatusCode.InternalServerError);
        var client = new VespaClient(_httpClient, _options);

        // Act
        var result = await client.HealthCheckAsync();

        // Assert
        Assert.False(result);
        VerifyHttpCall(HttpMethod.Get, "/state/v1/health");
    }

    [Fact]
    public async Task HealthCheckAsync_OnException_ReturnsFalse()
    {
        // Arrange
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new HttpRequestException("Network error"));

        var client = new VespaClient(_httpClient, _options);

        // Act
        var result = await client.HealthCheckAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task HealthCheckAsync_OnCallerCancellation_ThrowsOperationCanceledException()
    {
        // Same exception type as an HttpClient timeout — the caller's token state
        // is what decides between rethrowing and reporting unhealthy.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new TaskCanceledException());

        var client = new VespaClient(_httpClient, _options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.HealthCheckAsync(cts.Token));
    }

    [Fact]
    public async Task HealthCheckAsync_OnHttpClientTimeout_ReturnsFalse()
    {
        // HttpClient timeouts surface as TaskCanceledException even when the
        // caller's token was never cancelled — the probe must report unhealthy, not throw.
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new TaskCanceledException("timeout", new TimeoutException()));

        var client = new VespaClient(_httpClient, _options);

        Assert.False(await client.HealthCheckAsync());
    }

    [Fact]
    public async Task IsReadyAsync_OnHttpClientTimeout_ReturnsFalse()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new TaskCanceledException("timeout", new TimeoutException()));

        var client = new VespaClient(_httpClient, _options);

        Assert.False(await client.IsReadyAsync());
    }

    [Fact]
    public async Task GetVersionAsync_OnHttpClientTimeout_ReturnsNull()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new TaskCanceledException("timeout", new TimeoutException()));

        var client = new VespaClient(_httpClient, _options);

        Assert.Null(await client.GetVersionAsync());
    }

    #endregion

    #region GetMetricsAsync Tests (3 tests)

    [Fact]
    public async Task GetMetricsAsync_WithSuccess_ReturnsTypedVespaMetrics()
    {
        // Arrange — use the real /state/v1/metrics JSON structure
        const string json = """
            {
              "time": 1678000000,
              "status": { "code": "up" },
              "metrics": {
                "snapshot": { "from": 1234.0, "to": 5678.0 },
                "values": [
                  {
                    "name": "content.proton.documentdb.documents.total",
                    "values": { "last": 1234.0, "max": 1235.0, "sum": 12340.0, "count": 10 },
                    "dimensions": { "documenttype": "music" }
                  }
                ]
              }
            }
            """;
        SetupSuccessResponse(json);
        var client = new VespaClient(_httpClient, _options);

        // Act
        var result = await client.GetMetricsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1678000000L, result.Time);
        Assert.Equal("up", result.Status.Code);
        Assert.NotNull(result.Metrics);
        Assert.Single(result.Metrics!.Values);
        Assert.Equal("content.proton.documentdb.documents.total", result.Metrics.Values[0].Name);
        VerifyHttpCall(HttpMethod.Get, "/state/v1/metrics");
    }

    [Fact]
    public async Task GetMetricsAsync_WhenMetricsNodeNull_ReturnsStatusOnly()
    {
        // Arrange — metrics node absent
        const string json = """{"time":1000,"status":{"code":"up"}}""";
        SetupSuccessResponse(json);
        var client = new VespaClient(_httpClient, _options);

        // Act
        var result = await client.GetMetricsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal("up", result.Status.Code);
        Assert.Null(result.Metrics);
    }

    [Fact]
    public async Task GetMetricsAsync_OnHttpError_ReturnsNull()
    {
        // Arrange
        SetupErrorResponse(HttpStatusCode.ServiceUnavailable);
        var client = new VespaClient(_httpClient, _options);

        // Act
        var result = await client.GetMetricsAsync();

        // Assert
        Assert.Null(result);
        VerifyHttpCall(HttpMethod.Get, "/state/v1/metrics");
    }

    [Fact]
    public async Task GetMetricsAsync_OnException_ReturnsNull()
    {
        // Arrange
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new InvalidOperationException("Connection failed"));

        var client = new VespaClient(_httpClient, _options);

        // Act
        var result = await client.GetMetricsAsync();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetMetricsAsync_OnCancellation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new OperationCanceledException());

        var client = new VespaClient(_httpClient, _options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetMetricsAsync(cts.Token));
    }

    #endregion

    #region Helper Methods

    private void SetupSuccessResponse(string? content = null)
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content ?? "{}")
            });
    }

    private void SetupErrorResponse(HttpStatusCode statusCode)
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage(statusCode));
    }

    private void VerifyHttpCall(HttpMethod method, string expectedPath)
    {
        _mockHandler
            .Protected()
            .Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == method &&
                    req.RequestUri!.PathAndQuery == expectedPath
                ),
                ItExpr.IsAny<CancellationToken>()
            );
    }

    #endregion

    #region IsReadyAsync Tests (3 tests)

    [Fact]
    public async Task IsReadyAsync_WhenStatusCodeIsUp_ReturnsTrue()
    {
        // Arrange
        const string json = """{"status":{"code":"up"}}""";
        SetupSuccessResponse(json);
        var client = new VespaClient(_httpClient, _options);

        // Act
        var result = await client.IsReadyAsync();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task IsReadyAsync_WhenStatusCodeIsDown_ReturnsFalse()
    {
        // Arrange
        const string json = """{"status":{"code":"down"}}""";
        SetupSuccessResponse(json);
        var client = new VespaClient(_httpClient, _options);

        // Act
        var result = await client.IsReadyAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsReadyAsync_WhenHttpFails_ReturnsFalse()
    {
        // Arrange
        SetupErrorResponse(HttpStatusCode.ServiceUnavailable);
        var client = new VespaClient(_httpClient, _options);

        // Act
        var result = await client.IsReadyAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsReadyAsync_OnCancellation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new OperationCanceledException());

        var client = new VespaClient(_httpClient, _options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.IsReadyAsync(cts.Token));
    }

    #endregion

    #region DefaultRequestHeaders / ApiKey tests

    [Fact]
    public void Constructor_WithApiKey_SetsAuthorizationHeader()
    {
        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var opts = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            ApiKey = "my-secret-key"
        };

        _ = new VespaClient(http, opts);

        Assert.Equal("Bearer", http.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("my-secret-key", http.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Fact]
    public void Constructor_ApiKeyNotAppliedIfAuthorizationAlreadySet()
    {
        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "existing-token");

        var opts = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            ApiKey = "should-not-override"
        };

        _ = new VespaClient(http, opts);

        Assert.Equal("existing-token", http.DefaultRequestHeaders.Authorization?.Parameter);
    }

    [Fact]
    public void Constructor_WithDefaultRequestHeaders_AddsHeadersToHttpClient()
    {
        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var opts = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultRequestHeaders = new Dictionary<string, string>
            {
                ["X-Tenant-Id"] = "acme",
                ["X-Correlation-Id"] = "abc-123"
            }
        };

        _ = new VespaClient(http, opts);

        Assert.Contains("acme", http.DefaultRequestHeaders.GetValues("X-Tenant-Id"));
        Assert.Contains("abc-123", http.DefaultRequestHeaders.GetValues("X-Correlation-Id"));
    }

    [Fact]
    public void Constructor_WithApiKeyAndHeaders_AppliesThemToAdminClient()
    {
        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var opts = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            ConfigServerEndpoint = "http://localhost:19071",
            ApiKey = "my-secret-key",
            DefaultRequestHeaders = new Dictionary<string, string>
            {
                ["X-Tenant-Id"] = "acme"
            }
        };

        using var client = new VespaClient(http, opts);
        var adminOps = Assert.IsType<AdminOperations>(client.Admin);
        var adminHttpClient = (HttpClient?)typeof(AdminOperations)
            .GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(adminOps);

        Assert.NotNull(adminHttpClient);
        Assert.Equal("Bearer", adminHttpClient!.DefaultRequestHeaders.Authorization?.Scheme);
        Assert.Equal("my-secret-key", adminHttpClient.DefaultRequestHeaders.Authorization?.Parameter);
        Assert.Contains("acme", adminHttpClient.DefaultRequestHeaders.GetValues("X-Tenant-Id"));
    }

    [Fact]
    public void Constructor_WithNoCustomHeaders_DoesNotThrow()
    {
        using var http = new HttpClient(_mockHandler.Object) { BaseAddress = new Uri("http://localhost:8080") };
        var opts = new VespaClientOptions { Endpoint = "http://localhost:8080" };

        // ReSharper disable once AccessToDisposedClosure
        var ex = Record.Exception(() => new VespaClient(http, opts));

        Assert.Null(ex);
    }

    #endregion

    #region GetConfigAsync Tests (M12)

    [Fact]
    public async Task GetConfigAsync_ReturnsConfigGeneration()
    {
        const string json = """{"config":{"generation":42}}""";
        SetupSuccessResponse(json);
        var client = new VespaClient(_httpClient, _options);

        var result = await client.GetConfigAsync();

        Assert.NotNull(result);
        Assert.Equal(42, result.Config.Generation);
    }

    [Fact]
    public async Task GetConfigAsync_OnError_ReturnsNull()
    {
        SetupErrorResponse(HttpStatusCode.ServiceUnavailable);
        var client = new VespaClient(_httpClient, _options);

        var result = await client.GetConfigAsync();

        Assert.Null(result);
    }

    #endregion

    #region GetVersionAsync Tests (M12)

    [Fact]
    public async Task GetVersionAsync_ReturnsVersionString()
    {
        const string json = """{"version":"8.350.21"}""";
        SetupSuccessResponse(json);
        var client = new VespaClient(_httpClient, _options);

        var result = await client.GetVersionAsync();

        Assert.Equal("8.350.21", result);
    }

    [Fact]
    public async Task GetVersionAsync_OnError_ReturnsNull()
    {
        SetupErrorResponse(HttpStatusCode.ServiceUnavailable);
        var client = new VespaClient(_httpClient, _options);

        var result = await client.GetVersionAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task GetVersionAsync_OnCancellation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new OperationCanceledException());

        var client = new VespaClient(_httpClient, _options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetVersionAsync(cts.Token));
    }

    #endregion

    #region VespaHealthCheck Tests (M12)

    [Fact]
    public async Task VespaHealthCheck_WhenReady_ReturnsHealthy()
    {
        const string json = """{"status":{"code":"up"}}""";
        SetupSuccessResponse(json);
        var client = new VespaClient(_httpClient, _options);
        var healthCheck = new VespaHealthCheck(client);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task VespaHealthCheck_WhenNotReady_ReturnsUnhealthy()
    {
        SetupErrorResponse(HttpStatusCode.ServiceUnavailable);
        var client = new VespaClient(_httpClient, _options);
        var healthCheck = new VespaHealthCheck(client);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    #endregion

    #region GetHistogramsAsync Tests (M19)

    [Fact]
    public async Task GetHistogramsAsync_ReturnsRawContent()
    {
        const string csv = "# Histogram\nmetric1,100,200,300";
        SetupSuccessResponse(csv);
        var client = new VespaClient(_httpClient, _options);

        var result = await client.GetHistogramsAsync();

        Assert.NotNull(result);
        Assert.Contains("Histogram", result);
        VerifyHttpCall(HttpMethod.Get, "/state/v1/metrics/histograms");
    }

    [Fact]
    public async Task GetHistogramsAsync_OnError_ReturnsNull()
    {
        SetupErrorResponse(HttpStatusCode.ServiceUnavailable);
        var client = new VespaClient(_httpClient, _options);

        var result = await client.GetHistogramsAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task GetHistogramsAsync_OnException_ReturnsNull()
    {
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new HttpRequestException("Network error"));

        var client = new VespaClient(_httpClient, _options);

        var result = await client.GetHistogramsAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task GetHistogramsAsync_OnCancellation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ThrowsAsync(new OperationCanceledException());

        var client = new VespaClient(_httpClient, _options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetHistogramsAsync(cts.Token));
    }

    #endregion

    #region IAsyncDisposable / IDisposable Tests (M15)

    [Fact]
    public void VespaClient_ImplementsIDisposable()
    {
        var client = new VespaClient(_httpClient, _options);
        Assert.IsAssignableFrom<IDisposable>(client);
    }

    [Fact]
    public void VespaClient_ImplementsIAsyncDisposable()
    {
        var client = new VespaClient(_httpClient, _options);
        Assert.IsAssignableFrom<IAsyncDisposable>(client);
    }

    [Fact]
    public void VespaClient_Dispose_DoesNotThrow()
    {
        var client = new VespaClient(_httpClient, _options);
        var ex = Record.Exception(() => client.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task VespaClient_DisposeAsync_DoesNotThrow()
    {
        var client = new VespaClient(_httpClient, _options);
        var ex = await Record.ExceptionAsync(async () => await client.DisposeAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task VespaClient_AwaitUsing_Works()
    {
        await using var client = new VespaClient(_httpClient, _options);
        Assert.NotNull(client);
    }

    #endregion
}
