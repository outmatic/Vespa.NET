using Vespa.IntegrationTests.Fixtures;
using Vespa.Models;
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
    public async Task DeploySchemaAsync_Generic_SingleTypeRedeploy_RejectedBySchemaRemovalValidation()
    {
        if (!Enabled) return;

        // The shared app contains all VespaFixture.SchemaTypes; a single-type package
        // would drop the other schemas, which the config server rejects (data-loss
        // protection). The failed prepare leaves the active application untouched.
        var ex = await Assert.ThrowsAsync<VespaException>(
            () => fixture.Client.Admin.DeploySchemaAsync<TestProduct>());
        Assert.Contains("schema-removal", ex.Message);

        var ready = await fixture.Client.IsReadyAsync();
        Assert.True(ready);
    }

    // ── DeploySchemaAsync with multiple types ────────────────────────────────
    // Redeploys must carry the FULL fixture package: a package missing any deployed
    // schema is rejected by the config server's schema-removal validation.

    [Fact]
    public async Task DeploySchemaAsync_WithTypesList_DeploysSuccessfully()
    {
        if (!Enabled) return;

        // Re-deploy the same package via the IEnumerable<Type> overload — idempotent
        await fixture.Client.Admin.DeploySchemaAsync(VespaFixture.SchemaTypes);

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
        VespaSchemaBuilder.GenerateApplicationPackage(ms, VespaFixture.SchemaTypes);
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
