using Vespa.Documents;
using Vespa.IntegrationTests.Fixtures;
using Vespa.Models.Attributes;
using Vespa.Models.Schema;
using Vespa.Testcontainers;
using Xunit;

namespace Vespa.IntegrationTests;

public sealed class VespaContainerLifecycleTests
{
    [Fact]
    public async Task DisposalIsIdempotentAndPreventsStartingAgain()
    {
        await using var vespa = new VespaContainer();
        await vespa.StopAsync();
        await Task.WhenAll(vespa.DisposeAsync().AsTask(), vespa.DisposeAsync().AsTask());

        await Assert.ThrowsAsync<ObjectDisposedException>(() => vespa.StartAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => vespa.StopAsync());
        Assert.Throws<ObjectDisposedException>(() => vespa.Endpoint);
        Assert.Throws<ObjectDisposedException>(() => vespa.CreateClient());
    }

    [Fact]
    public async Task RepeatedStartAndRestartKeepTheDeployedApplicationAndItsData()
    {
        if (!VespaFixture.IntegrationEnabled) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        await using var vespa = new VespaContainer();
        await vespa.StartAsync(token);
        var endpoints = (vespa.Endpoint, vespa.ConfigEndpoint);
        var packagePath = Path.GetTempFileName();
        try
        {
            await using (var package = File.Create(packagePath))
                VespaSchemaBuilder.GenerateApplicationPackage<LifecycleItem>(package);
            await vespa.DeployApplicationAsync(packagePath, cancellationToken: token);
        }
        finally
        {
            File.Delete(packagePath);
        }

        var original = vespa.CreateClient();
        await original.Documents.PutAsync("retained", new LifecycleItem { Title = "still present" }, cancellationToken: token);
        await Task.WhenAll(vespa.StartAsync(token), vespa.StartAsync(token));
        Assert.Equal(endpoints, (vespa.Endpoint, vespa.ConfigEndpoint));
        Assert.Equal("still present", (await original.Documents.GetAsync<LifecycleItem>("retained", cancellationToken: token))?.Fields?.Title);

        await vespa.StopAsync(token);
        Assert.Throws<InvalidOperationException>(() => vespa.Endpoint);
        Assert.Throws<InvalidOperationException>(() => vespa.ConfigEndpoint);
        await vespa.StopAsync(token);

        await vespa.StartAsync(token);
        using var health = new HttpClient();
        await VespaContainer.WaitForApplicationReadyAsync(health, vespa.Endpoint, TimeSpan.FromMinutes(2), token);
        var restarted = vespa.CreateClient();
        Assert.Equal("still present", (await restarted.Documents.GetAsync<LifecycleItem>("retained", cancellationToken: token))?.Fields?.Title);
    }

    [VespaDocument("lifecycle_item", Mode = DocumentMode.StoreOnly)]
    private sealed class LifecycleItem
    {
        [VespaField(IndexingMode = IndexingMode.Summary)]
        public string Title { get; init; } = "";
    }
}
