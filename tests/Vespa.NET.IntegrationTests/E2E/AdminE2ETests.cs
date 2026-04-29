using Vespa.IntegrationTests.Fixtures;
using Vespa.Models.Schema;
using Xunit;

namespace Vespa.IntegrationTests.E2E;

/// <summary>
/// E2E tests for admin/schema deployment operations.
/// </summary>
[Collection("Vespa")]
[Trait("Category", "E2E")]
public class AdminE2ETests(VespaFixture fixture)
{
    private static bool Enabled => VespaFixture.IntegrationEnabled;

    // ── DeploySchemaAsync<T> ─────────────────────────────────────────────────

    [Fact]
    public async Task DeploySchemaAsync_Generic_DeploysSuccessfully()
    {
        if (!Enabled) return;

        // Re-deploy the same schema — idempotent operation
        await fixture.Client.Admin.DeploySchemaAsync<TestProduct>();

        // Verify the cluster is still healthy after deploy
        var ready = await fixture.Client.IsReadyAsync();
        Assert.True(ready);
    }

    // ── DeploySchemaAsync with multiple types ────────────────────────────────

    [Fact]
    public async Task DeploySchemaAsync_WithTypesList_DeploysSuccessfully()
    {
        if (!Enabled) return;

        // Deploy using the IEnumerable<Type> overload
        await fixture.Client.Admin.DeploySchemaAsync([typeof(TestProduct)]);

        var ready = await fixture.Client.IsReadyAsync();
        Assert.True(ready);
    }

    // ── DeployAsync with raw stream ──────────────────────────────────────────

    [Fact]
    public async Task DeployAsync_WithGeneratedPackage_DeploysSuccessfully()
    {
        if (!Enabled) return;

        // Generate a ZIP package in memory and deploy it
        using var ms = new MemoryStream();
        VespaSchemaBuilder.GenerateApplicationPackage<TestProduct>(ms);
        ms.Position = 0;

        await fixture.Client.Admin.DeployAsync(ms);

        var ready = await fixture.Client.IsReadyAsync();
        Assert.True(ready);
    }

    // ── GetApplicationStatusAsync ────────────────────────────────────────────

    [Fact]
    public async Task GetApplicationStatusAsync_ReturnsStatus()
    {
        if (!Enabled) return;

        var status = await fixture.Client.Admin.GetApplicationStatusAsync();
        Assert.NotNull(status);
    }
}
