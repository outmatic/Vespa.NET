using Vespa.IntegrationTests.Fixtures;
using Xunit;

namespace Vespa.IntegrationTests;

/// <summary>
/// Container-level and health/admin integration tests.
/// Uses the shared Vespa collection fixture (single container for all tests).
/// </summary>
[Collection("Vespa")]
[Trait("Category", "Integration")]
public class VespaContainerTests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    [Fact]
    public async Task HealthCheck_ReturnsTrue()
    {
        if (!Enabled) return;

        var healthy = await fixture.Client.HealthCheckAsync();
        Assert.True(healthy);
    }

    [Fact]
    public async Task IsReady_ReturnsTrue()
    {
        if (!Enabled) return;

        var ready = await fixture.Client.IsReadyAsync();
        Assert.True(ready);
    }

    [Fact]
    public void Endpoint_HasExpectedFormat()
    {
        if (!Enabled) return;

        // When using VESPA_ENDPOINT, the container is not created so Endpoint is null.
        // This test only applies when running via Testcontainers.
        if (fixture.Endpoint is null) return;

        Assert.StartsWith("http://", fixture.Endpoint);
    }

    [Fact]
    public async Task GetMetrics_DoesNotThrow()
    {
        if (!Enabled) return;

        var metrics = await fixture.Client.GetMetricsAsync();
        // bare container may not expose full metrics, but the call should not throw
        _ = metrics;
    }

    [Fact]
    public async Task GetVersion_ReturnsNonEmpty()
    {
        if (!Enabled) return;

        var version = await fixture.Client.GetVersionAsync();
        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Fact]
    public async Task GetConfig_ReturnsData()
    {
        if (!Enabled) return;

        var config = await fixture.Client.GetConfigAsync();
        Assert.NotNull(config);
    }

    [Fact]
    public async Task Admin_GetApplicationStatus_ReturnsData()
    {
        if (!Enabled) return;

        var status = await fixture.Client.Admin.GetApplicationStatusAsync();
        Assert.NotNull(status);
    }
}
