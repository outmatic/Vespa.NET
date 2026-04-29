using System.Net;
using System.Text;
using System.Text.Json;
using Moq;
using Moq.Protected;
using Vespa;
using Vespa.Admin;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Models.Schema;
using Xunit;

namespace Vespa.Tests.Unit;

public class AdminOperationsTests : IDisposable
{
    private readonly Mock<HttpMessageHandler> _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly AdminOperations _adminOps;

    public AdminOperationsTests()
    {
        _mockHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHandler.Object)
        {
            BaseAddress = new Uri("http://localhost:19071")
        };
        _adminOps = new AdminOperations(_httpClient, new VespaClientOptions
        {
            Endpoint = "http://localhost:8080"
        });
    }

    public void Dispose() => _httpClient.Dispose();

    // ── DeployAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeployAsync_SendsZipToCorrectEndpoint()
    {
        SetupResponse(HttpStatusCode.OK, "{}");
        using var fakeZip = new MemoryStream([0x50, 0x4B, 0x05, 0x06]);

        await _adminOps.DeployAsync(fakeZip);

        VerifyCall(
            HttpMethod.Post,
            "/application/v2/tenant/default/prepareandactivate",
            Times.Once());
    }

    [Fact]
    public async Task DeployAsync_SetsZipContentType()
    {
        string? contentType = null;
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
                contentType = req.Content?.Headers.ContentType?.MediaType)
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{}") });

        using var fakeZip = new MemoryStream([0x50, 0x4B]);
        await _adminOps.DeployAsync(fakeZip);

        Assert.Equal("application/zip", contentType);
    }

    [Fact]
    public async Task DeployAsync_OnHttpError_ThrowsVespaException()
    {
        SetupResponse(HttpStatusCode.BadRequest, "invalid package");

        using var fakeZip = new MemoryStream([0x50, 0x4B]);
        await Assert.ThrowsAnyAsync<VespaException>(
            () => _adminOps.DeployAsync(fakeZip));
    }

    // ── DeployAsync with tenant override ─────────────────────────────────────

    [Fact]
    public async Task DeployAsync_WithTenantOverride_UsesOverriddenTenant()
    {
        SetupResponse(HttpStatusCode.OK, "{}");
        using var fakeZip = new MemoryStream([0x50, 0x4B, 0x05, 0x06]);

        await _adminOps.DeployAsync(fakeZip, tenant: "acme");

        VerifyCall(
            HttpMethod.Post,
            "/application/v2/tenant/acme/prepareandactivate",
            Times.Once());
    }

    [Fact]
    public async Task DeployAsync_WithCustomTenantInOptions_UsesOptionsTenant()
    {
        SetupResponse(HttpStatusCode.OK, "{}");
        var ops = new AdminOperations(_httpClient, new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            Tenant = "mytenant"
        });
        using var fakeZip = new MemoryStream([0x50, 0x4B, 0x05, 0x06]);

        await ops.DeployAsync(fakeZip);

        VerifyCall(
            HttpMethod.Post,
            "/application/v2/tenant/mytenant/prepareandactivate",
            Times.Once());
    }

    // ── DeploySchemaAsync<T> ──────────────────────────────────────────────────

    [VespaDocument("song")]
    private record SongDoc
    {
        [VespaField(Name = "title", IndexingMode = IndexingMode.AttributeSummary)]
        public string Title { get; init; } = string.Empty;
    }

    [Fact]
    public async Task DeploySchemaAsync_T_PostsPackageToConfigServer()
    {
        SetupResponse(HttpStatusCode.OK, "{}");

        await _adminOps.DeploySchemaAsync<SongDoc>();

        VerifyCall(
            HttpMethod.Post,
            "/application/v2/tenant/default/prepareandactivate",
            Times.Once());
    }

    [Fact]
    public async Task DeploySchemaAsync_T_WithTenantOverride_UsesOverriddenTenant()
    {
        SetupResponse(HttpStatusCode.OK, "{}");

        await _adminOps.DeploySchemaAsync<SongDoc>(tenant: "acme");

        VerifyCall(
            HttpMethod.Post,
            "/application/v2/tenant/acme/prepareandactivate",
            Times.Once());
    }

    // ── GetApplicationStatusAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetApplicationStatusAsync_ReturnsDeserializedStatus()
    {
        var json = JsonSerializer.Serialize(new
        {
            tenant = "default",
            application = "default",
            environment = "prod",
            active = true
        });
        SetupResponse(HttpStatusCode.OK, json);

        var status = await _adminOps.GetApplicationStatusAsync();

        Assert.NotNull(status);
        Assert.Equal("default", status.Tenant);
        Assert.Equal("default", status.Application);
        Assert.Equal("prod", status.Environment);
        Assert.True(status.IsActive);
    }

    [Fact]
    public async Task GetApplicationStatusAsync_OnHttpError_ReturnsNull()
    {
        SetupResponse(HttpStatusCode.ServiceUnavailable, "");

        var status = await _adminOps.GetApplicationStatusAsync();

        Assert.Null(status);
    }

    [Fact]
    public async Task GetApplicationStatusAsync_WithTenantOverride_UsesOverriddenTenant()
    {
        SetupResponse(HttpStatusCode.OK, """{"tenant":"acme","application":"default","environment":"prod","active":true}""");

        var status = await _adminOps.GetApplicationStatusAsync(tenant: "acme");

        VerifyCall(
            HttpMethod.Get,
            "/application/v2/tenant/acme/application/default",
            Times.Once());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetupResponse(HttpStatusCode statusCode, string body)
    {
        _mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private void VerifyCall(HttpMethod method, string path, Times times)
    {
        _mockHandler.Protected().Verify(
            "SendAsync",
            times,
            ItExpr.Is<HttpRequestMessage>(r =>
                r.Method == method &&
                r.RequestUri!.PathAndQuery == path),
            ItExpr.IsAny<CancellationToken>());
    }
}
