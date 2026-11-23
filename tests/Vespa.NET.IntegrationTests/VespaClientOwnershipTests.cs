using Vespa.Admin;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Testcontainers;
using Xunit;

namespace Vespa.IntegrationTests;

public sealed class VespaClientOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixtureTeardownDisposesBothTransports(bool useExplicitEndpoint)
    {
        if (!VespaFixture.IntegrationEnabled) return;

        await using var external = useExplicitEndpoint ? new VespaContainer() : null;
        if (external is not null)
            await external.StartAsync();

        var fixture = new VespaFixture();
        try
        {
            await fixture.InitializeAsync(external?.Endpoint, external?.ConfigEndpoint);
            var client = fixture.Client;
            var admin = client.Admin;

            await fixture.DisposeAsync();

            await AssertClientDisposedAsync(client, admin);
            if (external is not null)
            {
                using var probe = new HttpClient();
                using var response = await probe.GetAsync(external.ConfigEndpoint + "/state/v1/health");
                Assert.True(response.IsSuccessStatusCode);
            }
        }
        finally
        {
            // Keep the regression test self-cleaning even with the old fixture.
            fixture.Client?.Dispose();
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task ContainerDisposesItsClientAndLeavesTheReturnedPairWithItsCaller()
    {
        if (!VespaFixture.IntegrationEnabled) return;

        await using var container = new VespaContainer();
        await container.StartAsync();
        var owned = container.CreateClient();
        var ownedAdmin = owned.Admin;
        var (client, http) = container.CreateClientWithHttp();
        using (http)
        using (client)
        {
            var admin = client.Admin;

            await container.DisposeAsync();

            await AssertClientDisposedAsync(owned, ownedAdmin);
            Assert.Same(admin, client.Admin);
            http.CancelPendingRequests();

            client.Dispose();
            await AssertAdminDisposedAsync(admin);
            http.CancelPendingRequests();
        }
    }

    private static async Task AssertClientDisposedAsync(VespaClient client, IAdminOperations admin)
    {
        // A canceled token prevents network I/O if a transport was left open.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.Documents.GetAsync<TestProduct>(
            "unused", "product", cancellationToken: new CancellationToken(canceled: true)));
        await AssertAdminDisposedAsync(admin);
        Assert.Throws<ObjectDisposedException>(() => client.Admin);
    }

    private static async Task AssertAdminDisposedAsync(IAdminOperations admin)
    {
        using var package = new MemoryStream();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            admin.DeployAsync(package, cancellationToken: new CancellationToken(canceled: true)));
    }
}
