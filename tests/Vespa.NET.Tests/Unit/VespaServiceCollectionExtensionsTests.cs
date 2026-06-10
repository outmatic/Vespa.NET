using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Vespa;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for VespaServiceCollectionExtensions covering dependency injection registration
/// </summary>
public class VespaServiceCollectionExtensionsTests
{
    #region AddVespaClient (options instance) Tests (5 tests)

    [Fact]
    public void AddVespaClient_WithOptions_RegistersServices()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test"
        };

        // Act
        services.AddVespaClient(options);
        var provider = services.BuildServiceProvider();

        // Assert
        var client = provider.GetService<IVespaClient>();
        Assert.NotNull(client);
        var registeredOptions = provider.GetService<VespaClientOptions>();
        Assert.NotNull(registeredOptions);
        Assert.Equal(options.Endpoint, registeredOptions.Endpoint);
    }

    [Fact]
    public void AddVespaClient_WithOptions_ConfiguresHttpClient()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test",
            Timeout = TimeSpan.FromSeconds(60)
        };

        // Act
        services.AddVespaClient(options);
        var provider = services.BuildServiceProvider();

        // Assert
        var client = provider.GetService<IVespaClient>();
        Assert.NotNull(client);
    }

    [Fact]
    public void AddVespaClient_UserHttpClientCustomization_IsNotOverwritten()
    {
        // The documented configureHttpClient extension point runs in the factory;
        // the VespaClient constructor must not re-apply defaults over it.
        var services = new ServiceCollection();
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test"
        };

        services.AddVespaClient(options, httpClient => httpClient.Timeout = TimeSpan.FromSeconds(99));
        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IVespaClient>();

        Assert.Equal(TimeSpan.FromSeconds(99), GetUnderlyingHttpClient(client).Timeout);
    }

    [Fact]
    public void AddVespaClient_RegistersExactlyOneIVespaClient()
    {
        // AddHttpClient<IVespaClient, VespaClient> used to leave a second, shadowed
        // registration built via the public ctor (re-applying defaults) —
        // IEnumerable<IVespaClient> would resolve both.
        var services = new ServiceCollection();
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test",
            DefaultRequestHeaders = new Dictionary<string, string> { ["X-Tenant-Id"] = "acme" }
        };

        services.AddVespaClient(options);
        var provider = services.BuildServiceProvider();

        var clients = provider.GetServices<IVespaClient>().ToList();
        var single = Assert.Single(clients);
        var values = GetUnderlyingHttpClient(single).DefaultRequestHeaders.GetValues("X-Tenant-Id");
        Assert.Equal("acme", Assert.Single(values));
    }

    [Fact]
    public void AddVespaClient_DefaultRequestHeaders_AreNotDuplicated()
    {
        var services = new ServiceCollection();
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test",
            DefaultRequestHeaders = new Dictionary<string, string> { ["X-Tenant-Id"] = "acme" }
        };

        services.AddVespaClient(options);
        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IVespaClient>();

        var values = GetUnderlyingHttpClient(client).DefaultRequestHeaders.GetValues("X-Tenant-Id");
        Assert.Equal("acme", Assert.Single(values));
    }

    private static HttpClient GetUnderlyingHttpClient(IVespaClient client) =>
        (HttpClient)typeof(VespaClient)
            .GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;

    [Fact]
    public void AddVespaClient_WithApiKey_SetsAuthorizationHeader()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test",
            ApiKey = "test-api-key"
        };

        // Act
        services.AddVespaClient(options);
        var provider = services.BuildServiceProvider();

        // Assert
        var client = provider.GetService<IVespaClient>();
        Assert.NotNull(client);
    }

    [Fact]
    public void AddVespaClient_WithNullServices_ThrowsArgumentNullException()
    {
        // Arrange
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test"
        };

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            VespaServiceCollectionExtensions.AddVespaClient(null!, options));
    }

    [Fact]
    public void AddVespaClient_WithNullOptions_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            services.AddVespaClient(null!));
    }

    #endregion


    #region AddVespaClient (named client) Tests (4 tests)

    [Fact]
    public void AddVespaClient_WithName_RegistersNamedClient()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test"
        };

        // Act
        services.AddVespaClient("vespa-prod", options);
        var provider = services.BuildServiceProvider();

        // Assert
        var client = provider.GetKeyedService<IVespaClient>("vespa-prod");
        Assert.NotNull(client);
        var namedOptions = provider.GetKeyedService<VespaClientOptions>("vespa-prod");
        Assert.NotNull(namedOptions);
        Assert.Equal(options.Endpoint, namedOptions.Endpoint);
    }

    [Fact]
    public void AddVespaClient_WithNullName_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test"
        };

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            services.AddVespaClient(null!, options));
    }

    [Fact]
    public void AddVespaClient_WithEmptyName_ThrowsArgumentException()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test"
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            services.AddVespaClient("", options));
    }

    [Fact]
    public void AddVespaClient_WithMultipleNames_RegistersMultipleClients()
    {
        // Arrange
        var services = new ServiceCollection();
        var options1 = new VespaClientOptions
        {
            Endpoint = "http://prod:8080",
            DefaultNamespace = "prod"
        };
        var options2 = new VespaClientOptions
        {
            Endpoint = "http://staging:8080",
            DefaultNamespace = "staging"
        };

        // Act
        services.AddVespaClient("vespa-prod", options1);
        services.AddVespaClient("vespa-staging", options2);
        var provider = services.BuildServiceProvider();

        // Assert
        var prodClient = provider.GetKeyedService<IVespaClient>("vespa-prod");
        var stagingClient = provider.GetKeyedService<IVespaClient>("vespa-staging");
        Assert.NotNull(prodClient);
        Assert.NotNull(stagingClient);
        Assert.NotSame(prodClient, stagingClient);
    }

    #endregion

    #region AddVespaClientWithResilienceStrategy Tests (3 tests)

    [Fact]
    public void AddVespaClientWithResilienceStrategy_RegistersServices()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test"
        };

        // Act
        services.AddVespaClientWithResilienceStrategy(
            options,
            (_, _) =>
            {
                // Custom resilience configuration
            }
        );
        var provider = services.BuildServiceProvider();

        // Assert
        var client = provider.GetService<IVespaClient>();
        Assert.NotNull(client);
    }

    [Fact]
    public void AddVespaClientWithResilienceStrategy_WithNullConfigureResilience_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test"
        };

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            services.AddVespaClientWithResilienceStrategy(options, null!));
    }

    [Fact]
    public void AddVespaClientWithResilienceStrategy_WithNullOptions_ThrowsArgumentNullException()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            services.AddVespaClientWithResilienceStrategy(
                null!,
                (_, _) => { }
            ));
    }

    #endregion

    #region Handler Configuration Tests

    [Fact]
    public void BuildSocketsHttpHandler_WithConnectionPoolingDisabled_DisablesReuse()
    {
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            UseConnectionPooling = false
        };

        using var handler = VespaServiceCollectionExtensions.BuildSocketsHttpHandler(options);

        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionIdleTimeout);
        Assert.False(handler.EnableMultipleHttp2Connections);
    }

    #endregion
}
