using System.Net;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Vespa;
using Vespa.Tests.Helpers;
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

    #region Repeated registration / options collision Tests

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddVespaClient_DefaultHttpName_IsRejectedBeforeChangingTheCollection(bool registerDefaultFirst)
    {
        var services = new ServiceCollection();
        var named = new VespaClientOptions { Endpoint = "http://named:8080" };
        var unnamed = new VespaClientOptions { Endpoint = "http://unnamed:8080" };
        void RegisterUnnamed() => services.AddVespaClient(unnamed,
            client => client.DefaultRequestHeaders.Add("X-Unnamed", "unnamed"));
        if (registerDefaultFirst)
            RegisterUnnamed();

        var before = services.ToArray();
        Assert.Throws<ArgumentException>("name", () => services.AddVespaClient("Vespa.NET", named));
        Assert.Equal(before, services.ToArray());

        if (!registerDefaultFirst)
            RegisterUnnamed();

        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetKeyedService<IVespaClient>("Vespa.NET"));
        using var http = GetUnderlyingHttpClient(provider.GetRequiredService<IVespaClient>());
        Assert.Equal(new Uri(unnamed.Endpoint), http.BaseAddress);
        Assert.Equal("unnamed", Assert.Single(http.DefaultRequestHeaders.GetValues("X-Unnamed")));
        AssertSingleVespaHandlerChain(provider, "Vespa.NET");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddVespaClient_CaseVariant_KeepsTransportAndRetryIndependent(bool registerDefaultFirst)
    {
        const string namedKey = "vespa.net";
        var services = new ServiceCollection();
        var unnamedOptions = new VespaClientOptions { Endpoint = "http://unnamed:8080" };
        var namedOptions = new VespaClientOptions { Endpoint = "http://named:8080", EnableRetry = false };
        using var unnamedHandler = new MockHttpMessageHandler();
        using var namedHandler = new MockHttpMessageHandler();
        unnamedHandler.QueueResponses(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), new HttpResponseMessage(HttpStatusCode.OK));
        namedHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        void RegisterUnnamed() => services.AddVespaClientWithResilienceStrategy(unnamedOptions,
            (pipeline, _) => pipeline.AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 1, Delay = TimeSpan.Zero }),
            client => client.DefaultRequestHeaders.Add("X-Client", "unnamed"))
            .ConfigurePrimaryHttpMessageHandler(() => unnamedHandler);

        void RegisterNamed() => Assert.Equal(namedKey, services.AddVespaClient(namedKey, namedOptions,
            client => client.DefaultRequestHeaders.Add("X-Client", "named"))
            .ConfigurePrimaryHttpMessageHandler(() => namedHandler).Name);

        if (registerDefaultFirst)
        {
            RegisterUnnamed();
            RegisterNamed();
        }
        else
        {
            RegisterNamed();
            RegisterUnnamed();
        }

        using var provider = services.BuildServiceProvider();
        var unnamed = provider.GetRequiredService<IVespaClient>();
        var named = provider.GetRequiredKeyedService<IVespaClient>(namedKey);
        using var unnamedHttp = GetUnderlyingHttpClient(unnamed);
        using var namedHttp = GetUnderlyingHttpClient(named);
        using var factoryHttp = provider.GetRequiredService<IHttpClientFactory>().CreateClient(namedKey);

        Assert.True(await unnamed.HealthCheckAsync());
        Assert.False(await named.HealthCheckAsync());
        Assert.Equal(2, unnamedHandler.Requests.Count);
        Assert.All(unnamedHandler.Requests, request =>
        {
            Assert.Equal("unnamed", request.RequestUri!.Host);
            Assert.Equal("unnamed", Assert.Single(request.Headers.GetValues("X-Client")));
        });
        var namedRequest = Assert.Single(namedHandler.Requests);
        Assert.Equal("named", namedRequest.RequestUri!.Host);
        Assert.Equal("named", Assert.Single(namedRequest.Headers.GetValues("X-Client")));
        Assert.Equal(new Uri(namedOptions.Endpoint), factoryHttp.BaseAddress);
        Assert.Equal("named", Assert.Single(factoryHttp.DefaultRequestHeaders.GetValues("X-Client")));
        AssertSingleVespaHandlerChain(provider, "Vespa.NET");
        AssertSingleVespaHandlerChain(provider, namedKey);
    }

    private static void AssertSingleVespaHandlerChain(IServiceProvider provider, string name)
    {
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
        var chain = new List<DelegatingHandler>();
        while (handler is DelegatingHandler next)
        {
            chain.Add(next);
            handler = next.InnerHandler!;
        }
        Assert.Single(chain.OfType<VespaMetricsHandler>());
        Assert.Single(chain.OfType<ResilienceHandler>());
    }

    [Fact]
    public void AddVespaClient_CalledRepeatedly_UsesOnlyTheLastConfiguration()
    {
        var services = new ServiceCollection();
        services.AddVespaClient(new VespaClientOptions
        {
            Endpoint = "http://original:8080",
            ApiKey = "old-key",
            DefaultRequestHeaders = new Dictionary<string, string> { ["X-Old"] = "old" }
        }, client => client.DefaultRequestHeaders.Add("X-Old-Callback", "old"));
        services.AddVespaClient(new VespaClientOptions { Endpoint = "http://intermediate:8080" });

        var options = new VespaClientOptions
        {
            Endpoint = "http://replacement:8080",
            Timeout = TimeSpan.FromSeconds(42),
            DefaultRequestHeaders = new Dictionary<string, string> { ["X-Tenant"] = "new" }
        };
        services.AddVespaClient(options, client => client.DefaultRequestHeaders.Add("X-New-Callback", "new"));
        using var provider = services.BuildServiceProvider();

        var httpClient = GetUnderlyingHttpClient(Assert.Single(provider.GetServices<IVespaClient>()));
        Assert.Equal(new Uri(options.Endpoint), httpClient.BaseAddress);
        Assert.Equal(options.Timeout, httpClient.Timeout);
        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
        Assert.False(httpClient.DefaultRequestHeaders.Contains("X-Old"));
        Assert.False(httpClient.DefaultRequestHeaders.Contains("X-Old-Callback"));
        Assert.Equal("new", Assert.Single(httpClient.DefaultRequestHeaders.GetValues("X-Tenant")));
        Assert.Equal("new", Assert.Single(httpClient.DefaultRequestHeaders.GetValues("X-New-Callback")));
        Assert.Same(options, Assert.Single(provider.GetServices<VespaClientOptions>()));
    }

    [Fact]
    public async Task AddVespaClientWithResilienceStrategy_AfterDefault_ReplacesRetryPolicy()
    {
        var services = new ServiceCollection();
        services.AddVespaClient(new VespaClientOptions { Endpoint = "http://original:8080", MaxRetryAttempts = 5 });
        var handler = new MockHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var options = new VespaClientOptions { Endpoint = "http://replacement:8080" };
        services.AddVespaClientWithResilienceStrategy(options, (_, _) => { })
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();

        var httpClient = GetUnderlyingHttpClient(Assert.Single(provider.GetServices<IVespaClient>()));
        using var response = await httpClient.GetAsync("/state/v1/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Same(options, provider.GetRequiredService<VespaClientOptions>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddVespaClient_AfterPreviousStrategy_UsesOnlyTheLatestRetryCount(bool customFirst)
    {
        var services = new ServiceCollection();
        var oldOptions = new VespaClientOptions { Endpoint = "http://original:8080", MaxRetryAttempts = 5 };
        if (customFirst)
            services.AddVespaClientWithResilienceStrategy(oldOptions, (_, _) =>
                throw new InvalidOperationException("The previous strategy must not be configured."));
        else
            services.AddVespaClient(oldOptions);

        var handler = new MockHttpMessageHandler();
        handler.QueueResponses(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.OK));
        services.AddVespaClient(new VespaClientOptions
        {
            Endpoint = "http://replacement:8080",
            MaxRetryAttempts = 1,
            EnableCircuitBreaker = false
        }).ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();

        var httpClient = GetUnderlyingHttpClient(Assert.Single(provider.GetServices<IVespaClient>()));
        using var response = await httpClient.GetAsync("/state/v1/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, 4)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 4)]
    [InlineData(HttpStatusCode.InternalServerError, 2)]
    [InlineData(HttpStatusCode.BadRequest, 1)]
    public async Task AddVespaClient_BulkPut_RetriesAtTheHttpAndFeedLevels(HttpStatusCode status, int expectedRequests)
    {
        var handler = new MockHttpMessageHandler();
        for (var i = 0; i < expectedRequests; i++)
            handler.QueueResponse(new HttpResponseMessage(status));

        var services = new ServiceCollection();
        services.AddVespaClient(new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            MaxRetryAttempts = 1,
            FeedRetryBaseDelay = TimeSpan.FromMilliseconds(1),
            EnableCircuitBreaker = false
        }).ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IVespaClient>();

        var result = await client.Feed.BulkPutAsync(
            [TestDataFactory.CreateFeedDocument("doc-1", new { name = "Test" })], "testdoc");

        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(1, result.FailureCount);
        Assert.Equal((int)status, Assert.Single(result.Errors).StatusCode);
        Assert.Equal(expectedRequests, handler.Requests.Count);
    }

    [Fact]
    public async Task AddVespaClient_UsesConfiguredCircuitBreakerThroughput()
    {
        using var configuration = new ConfigurationManager
        {
            ["Endpoint"] = "http://localhost:8080",
            ["EnableRetry"] = "false",
            ["CircuitBreakerMinimumThroughput"] = "4"
        };
        var options = configuration.Get<VespaClientOptions>(binder => binder.ErrorOnUnknownConfiguration = true)!;
        var handler = new MockHttpMessageHandler();
        HttpStatusCode[] sampledStatuses =
        [
            HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable
        ];
        handler.QueueResponses(sampledStatuses.Select(status => new HttpResponseMessage(status)).ToArray());
        var services = new ServiceCollection();
        var registration = services.AddVespaClient(options).ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(registration.Name);

        foreach (var status in sampledStatuses)
        {
            using var response = await client.GetAsync("/state/v1/health");
            Assert.Equal(status, response.StatusCode);
        }

        await Assert.ThrowsAnyAsync<BrokenCircuitException>(() => client.GetAsync("/state/v1/health"));
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task AddVespaClientWithResilienceStrategy_CalledTwice_UsesOnlyTheLastStrategy()
    {
        var services = new ServiceCollection();
        var options = new VespaClientOptions { Endpoint = "http://localhost:8080" };
        services.AddVespaClientWithResilienceStrategy(options, (_, _) =>
            throw new InvalidOperationException("The previous strategy must not be configured."));

        var handler = new MockHttpMessageHandler();
        handler.QueueResponses(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.OK));
        services.AddVespaClientWithResilienceStrategy(options, (pipeline, _) =>
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 1,
                Delay = TimeSpan.Zero,
                UseJitter = false
            })).ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();

        var httpClient = GetUnderlyingHttpClient(Assert.Single(provider.GetServices<IVespaClient>()));
        using var response = await httpClient.GetAsync("/state/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void AddVespaClient_ReplacingNamedClient_PreservesOtherClientsAndHttpServices()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("external", client => client.BaseAddress = new Uri("http://external:8080"))
            .AddResilienceHandler("external-policy", (_, _) => { });
        services.AddVespaClient("a", new VespaClientOptions { Endpoint = "http://a:8080" });
        services.AddVespaClient("b", new VespaClientOptions { Endpoint = "http://b:8080" });
        services.AddVespaClient(new VespaClientOptions { Endpoint = "http://localhost:8080" });
        var options = new VespaClientOptions { Endpoint = "http://replacement:8080" };
        services.AddVespaClient("a", options);
        services.AddVespaClient("a", options);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(new Uri(options.Endpoint),
            GetUnderlyingHttpClient(Assert.Single(provider.GetKeyedServices<IVespaClient>("a"))).BaseAddress);
        Assert.Same(options, Assert.Single(provider.GetKeyedServices<VespaClientOptions>("a")));
        Assert.Equal(new Uri("http://b:8080"), GetUnderlyingHttpClient(provider.GetRequiredKeyedService<IVespaClient>("b")).BaseAddress);
        Assert.Equal(new Uri("http://localhost:8080"), GetUnderlyingHttpClient(provider.GetRequiredService<IVespaClient>()).BaseAddress);
        using var external = provider.GetRequiredService<IHttpClientFactory>().CreateClient("external");
        Assert.Equal(new Uri("http://external:8080"), external.BaseAddress);
    }

    [Fact]
    public void AddVespaClient_DoesNotClobberConsumerRegisteredOptionsSingleton()
    {
        // The client factory captures the options in a closure; the container
        // registration is diagnostics-only (TryAddSingleton) and never wins over
        // a consumer's own VespaClientOptions singleton.
        var services = new ServiceCollection();
        var consumerOptions = new VespaClientOptions { Endpoint = "http://consumer:9999" };
        services.AddSingleton(consumerOptions);

        var vespaOptions = new VespaClientOptions { Endpoint = "http://localhost:8080" };
        services.AddVespaClient(new VespaClientOptions { Endpoint = "http://original:8080" });
        services.AddVespaClient(vespaOptions);

        var provider = services.BuildServiceProvider();
        Assert.Same(consumerOptions, provider.GetRequiredService<VespaClientOptions>());

        var client = provider.GetRequiredService<IVespaClient>();
        Assert.Equal(new Uri("http://localhost:8080"), GetUnderlyingHttpClient(client).BaseAddress);
    }

    [Fact]
    public void AddVespaClient_InvalidReplacement_PreservesPreviousConfiguration()
    {
        var services = new ServiceCollection();
        var options = new VespaClientOptions { Endpoint = "http://original:8080" };
        services.AddVespaClient(options);

        Assert.Throws<ArgumentException>(() => services.AddVespaClient(new VespaClientOptions { Endpoint = "relative" }));
        using var provider = services.BuildServiceProvider();
        Assert.Same(options, provider.GetRequiredService<VespaClientOptions>());
        Assert.Equal(new Uri(options.Endpoint), GetUnderlyingHttpClient(provider.GetRequiredService<IVespaClient>()).BaseAddress);
    }

    [Fact]
    public void AddVespaClient_Replacement_DoesNotChangeAnExistingProvider()
    {
        var services = new ServiceCollection();
        services.AddVespaClient(new VespaClientOptions { Endpoint = "http://original:8080" });
        using var originalProvider = services.BuildServiceProvider();
        services.AddVespaClient(new VespaClientOptions { Endpoint = "http://replacement:8080" });
        using var replacementProvider = services.BuildServiceProvider();

        Assert.Equal(new Uri("http://original:8080"),
            GetUnderlyingHttpClient(originalProvider.GetRequiredService<IVespaClient>()).BaseAddress);
        Assert.Equal(new Uri("http://replacement:8080"),
            GetUnderlyingHttpClient(replacementProvider.GetRequiredService<IVespaClient>()).BaseAddress);
    }

    [Fact]
    public void AddVespaClient_Replacement_ConfiguresTransportFromLatestOptions()
    {
        var services = new ServiceCollection();
        services.AddVespaClient(new VespaClientOptions { Endpoint = "http://original:8080", UseCompression = true });
        var builder = services.AddVespaClient(new VespaClientOptions
        {
            Endpoint = "http://replacement:8080",
            UseCompression = false,
            UseConnectionPooling = false
        });
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var handlerBuilder = scope.ServiceProvider.GetRequiredService<HttpMessageHandlerBuilder>();
        handlerBuilder.Name = builder.Name;
        var factoryOptions = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(builder.Name);
        foreach (var action in factoryOptions.HttpMessageHandlerBuilderActions)
            action(handlerBuilder);
        using var handler = Assert.IsType<SocketsHttpHandler>(handlerBuilder.PrimaryHandler);

        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionLifetime);
        Assert.Single(handlerBuilder.AdditionalHandlers.OfType<ResilienceHandler>());
    }

    #endregion

    #region Health check registration Tests

    [Fact]
    public void AddVespa_WithClientName_ResolvesKeyedClient()
    {
        var services = new ServiceCollection();
        services.AddVespaClient("orders", new VespaClientOptions { Endpoint = "http://localhost:8080" });
        services.AddHealthChecks().AddVespa(clientName: "orders", name: "vespa-orders");

        var provider = services.BuildServiceProvider();
        var registrations = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        // Materializing the health check resolves the keyed IVespaClient("orders")
        var registration = Assert.Single(registrations, r => r.Name == "vespa-orders");
        Assert.IsType<VespaHealthCheck>(registration.Factory(provider));
    }

    [Fact]
    public void AddVespa_Unnamed_ResolvesDefaultClient()
    {
        var services = new ServiceCollection();
        services.AddVespaClient(new VespaClientOptions { Endpoint = "http://localhost:8080" });
        services.AddHealthChecks().AddVespa();

        var provider = services.BuildServiceProvider();
        var registrations = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        var registration = Assert.Single(registrations, r => r.Name == "vespa");
        Assert.IsType<VespaHealthCheck>(registration.Factory(provider));
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
