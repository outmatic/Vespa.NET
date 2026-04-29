using Vespa;
using Xunit;

namespace Vespa.Tests;

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
    public void DefaultValues_AreSetCorrectly()
    {
        // Arrange & Act
        var options = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080"
        };

        // Assert
        Assert.Equal("default", options.DefaultNamespace);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
        Assert.Equal(100, options.MaxConnectionsPerServer);
        Assert.True(options.EnableRetry);
        Assert.Equal(3, options.MaxRetryAttempts);
        Assert.True(options.EnableCircuitBreaker);
        Assert.True(options.UseConnectionPooling);
        Assert.True(options.UseCompression);
    }
}
