using Vespa;
using Xunit;

namespace Vespa.Tests.Unit;

public class VespaClientOptionsTests
{
    [Fact]
    public void Validate_ThrowsException_WhenEndpointIsNull()
    {
        // Arrange
        var options = new VespaClientOptions
        {
            Endpoint = null!
        };

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => options.Validate());
    }

    [Fact]
    public void Validate_ThrowsException_WhenEndpointIsInvalid()
    {
        // Arrange
        var options = new VespaClientOptions
        {
            Endpoint = "not-a-valid-url"
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Succeeds_WhenOptionsAreValid()
    {
        // Arrange
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = "test"
        };

        // Act & Assert (no exception should be thrown)
        options.Validate();
    }

    [Fact]
    public void Validate_ThrowsException_WhenConnectTimeoutIsZero()
    {
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            ConnectTimeout = TimeSpan.Zero
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void Validate_ThrowsException_WhenCircuitBreakerThresholdBelowPollyMinimum()
    {
        // Polly's HttpCircuitBreakerStrategyOptions requires MinimumThroughput >= 2;
        // a threshold of 1 passed registration and blew up on the first request.
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            CircuitBreakerFailureThreshold = 1
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void Validate_AllowsThresholdOfOne_WhenCircuitBreakerDisabled()
    {
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            EnableCircuitBreaker = false,
            CircuitBreakerFailureThreshold = 1
        };

        options.Validate();
    }

    [Theory]
    [InlineData("ftp://localhost:8080")]
    [InlineData("file:///tmp/vespa")]
    [InlineData("localhost:8080")] // parses as absolute URI with scheme "localhost"
    [InlineData("vespa://cluster")]
    public void Validate_ThrowsException_WhenEndpointSchemeIsNotHttp(string endpoint)
    {
        var options = new VespaClientOptions { Endpoint = endpoint };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Equal(nameof(VespaClientOptions.Endpoint), ex.ParamName);
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("https://myapp.vespa-cloud.com")]
    public void Validate_Succeeds_ForHttpAndHttpsEndpoints(string endpoint)
    {
        new VespaClientOptions { Endpoint = endpoint }.Validate();
    }

    [Theory]
    [InlineData("ftp://localhost:19071")]
    [InlineData("localhost:19071")]
    [InlineData("not a url")]
    public void Validate_ThrowsException_WhenConfigServerEndpointIsNotHttp(string configEndpoint)
    {
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            ConfigServerEndpoint = configEndpoint
        };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Equal(nameof(VespaClientOptions.ConfigServerEndpoint), ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ThrowsException_WhenTenantIsEmptyOrWhitespace(string tenant)
    {
        // An empty tenant would produce malformed "//" segments in /application/v2 paths.
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            Tenant = tenant
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ThrowsException_WhenDefaultNamespaceIsEmptyOrWhitespace(string ns)
    {
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            DefaultNamespace = ns
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void DefaultValues_AreSetCorrectly()
    {
        // Arrange & Act
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080"
        };

        // Assert
        Assert.Equal("default", options.DefaultNamespace);
        Assert.Equal("default", options.Tenant);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.Equal(100, options.MaxConnectionsPerServer);
        Assert.True(options.EnableRetry);
        Assert.Equal(3, options.MaxRetryAttempts);
        Assert.True(options.EnableCircuitBreaker);
        Assert.True(options.UseConnectionPooling);
        Assert.True(options.UseCompression);
        // Privacy/behavior-preserving defaults: no proxy, no query text in traces
        Assert.False(options.UseProxy);
        Assert.False(options.RecordQueryTextInTraces);
    }

    [Fact]
    public void BuildSocketsHttpHandler_HonorsUseProxy()
    {
        using var offHandler = VespaServiceCollectionExtensions.BuildSocketsHttpHandler(
            new VespaClientOptions { Endpoint = "http://localhost:8080" });
        using var onHandler = VespaServiceCollectionExtensions.BuildSocketsHttpHandler(
            new VespaClientOptions { Endpoint = "http://localhost:8080", UseProxy = true });

        Assert.False(offHandler.UseProxy);
        Assert.True(onHandler.UseProxy);
    }
}
