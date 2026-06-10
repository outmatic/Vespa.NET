using System.Text.Json;
using Vespa.Models;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for VespaException and error handling scenarios
/// </summary>
public class ErrorHandlingTests
{
    #region VespaException Tests (6 tests)

    [Fact]
    public void VespaException_WithMessageOnly_SetsMessageCorrectly()
    {
        // Arrange
        var message = "Test error message";

        // Act
        var exception = new VespaException(message);

        // Assert
        Assert.Equal(message, exception.Message);
        Assert.Null(exception.StatusCode);
        Assert.Null(exception.Error);
    }

    [Fact]
    public void VespaException_WithStatusCode_SetsStatusCodeCorrectly()
    {
        // Arrange
        var message = "Test error";
        var statusCode = 500;

        // Act
        var exception = new VespaException(message, statusCode);

        // Assert
        Assert.Equal(message, exception.Message);
        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Null(exception.Error);
    }

    [Fact]
    public void VespaException_WithVespaError_SetsErrorDetailsCorrectly()
    {
        // Arrange
        var message = "Test error";
        var error = new VespaError
        {
            Code = 400,
            Message = "Bad request",
            Summary = "Invalid query"
        };

        // Act
        var exception = new VespaException(message, error, 400);

        // Assert
        Assert.Equal(message, exception.Message);
        Assert.Equal(400, exception.StatusCode);
        Assert.NotNull(exception.Error);
        Assert.Equal(error.Code, exception.Error.Code);
        Assert.Equal(error.Message, exception.Error.Message);
        Assert.Equal(error.Summary, exception.Error.Summary);
    }

    [Fact]
    public void VespaException_WithVespaErrorAndNoStatusCode_SetsErrorCorrectly()
    {
        // Arrange
        var message = "Test error";
        var error = new VespaError
        {
            Code = 500,
            Message = "Internal error"
        };

        // Act
        var exception = new VespaException(message, error);

        // Assert
        Assert.Equal(message, exception.Message);
        Assert.NotNull(exception.Error);
        Assert.Equal(error.Code, exception.Error.Code);
    }

    [Fact]
    public void VespaException_CanBeThrown()
    {
        // Arrange & Act
        void ThrowException() => throw new VespaException("Test error", 500);

        // Assert
        var ex = Assert.Throws<VespaException>(ThrowException);
        Assert.Equal("Test error", ex.Message);
        Assert.Equal(500, ex.StatusCode);
    }

    [Fact]
    public async Task VespaException_CanBeCaughtInAsyncContext()
    {
        // Arrange & Act & Assert
        var ex = await Assert.ThrowsAsync<VespaException>(async () =>
        {
            await Task.Yield();
            throw new VespaException("Async error", 503);
        });

        Assert.Equal("Async error", ex.Message);
        Assert.Equal(503, ex.StatusCode);
    }

    #endregion

    #region VespaError Deserialization Tests (6 tests)

    [Fact]
    public void VespaError_Deserialize_WithAllFields_ParsesCorrectly()
    {
        // Arrange
        var json = @"{
            ""code"": 400,
            ""message"": ""Bad request"",
            ""summary"": ""Invalid query syntax"",
            ""details"": ""Column 'unknown' does not exist"",
            ""stackTrace"": ""at VespaQuery.Parse()""
        }";

        // Act
        var error = JsonSerializer.Deserialize<VespaError>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Assert
        Assert.NotNull(error);
        Assert.Equal(400, error.Code);
        Assert.Equal("Bad request", error.Message);
        Assert.Equal("Invalid query syntax", error.Summary);
        Assert.Equal("Column 'unknown' does not exist", error.Details);
        Assert.Equal("at VespaQuery.Parse()", error.StackTrace);
    }

    [Fact]
    public void VespaError_Deserialize_WithMinimalFields_ParsesCorrectly()
    {
        // Arrange
        var json = @"{
            ""code"": 404,
            ""message"": ""Document not found""
        }";

        // Act
        var error = JsonSerializer.Deserialize<VespaError>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Assert
        Assert.NotNull(error);
        Assert.Equal(404, error.Code);
        Assert.Equal("Document not found", error.Message);
        Assert.Null(error.Summary);
        Assert.Null(error.Details);
    }

    [Fact]
    public void VespaError_Serialize_RoundTrip_PreservesData()
    {
        // Arrange
        var original = new VespaError
        {
            Code = 500,
            Message = "Internal server error",
            Summary = "Database connection failed",
            Details = "Connection timeout after 30s"
        };

        // Act
        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var deserialized = JsonSerializer.Deserialize<VespaError>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.Code, deserialized.Code);
        Assert.Equal(original.Message, deserialized.Message);
        Assert.Equal(original.Summary, deserialized.Summary);
        Assert.Equal(original.Details, deserialized.Details);
    }

    [Theory]
    [InlineData(400, "Bad Request")]
    [InlineData(401, "Unauthorized")]
    [InlineData(403, "Forbidden")]
    [InlineData(404, "Not Found")]
    [InlineData(500, "Internal Server Error")]
    [InlineData(503, "Service Unavailable")]
    public void VespaError_WithVariousStatusCodes_DeserializesCorrectly(int code, string message)
    {
        // Arrange
        var json = $@"{{""code"": {code}, ""message"": ""{message}""}}";

        // Act
        var error = JsonSerializer.Deserialize<VespaError>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Assert
        Assert.NotNull(error);
        Assert.Equal(code, error.Code);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void VespaError_WithEmptyJson_CreatesEmptyError()
    {
        // Arrange
        var json = "{}";

        // Act
        var error = JsonSerializer.Deserialize<VespaError>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Assert
        Assert.NotNull(error);
        Assert.Equal(0, error.Code);
        Assert.Empty(error.Message);
    }

    [Fact]
    public void VespaError_MalformedJson_ThrowsJsonException()
    {
        // Arrange
        var json = "{invalid json";

        // Act & Assert
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<VespaError>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
    }

    #endregion

    #region Typed Exception Hierarchy (13 tests)

    // ── FromStatusCode factory ────────────────────────────────────────────────

    [Fact]
    public void FromStatusCode_404_ReturnsVespaNotFoundException()
    {
        var ex = VespaException.FromStatusCode(404, "not found");
        Assert.IsType<VespaNotFoundException>(ex);
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public void FromStatusCode_412_ReturnsVespaConditionNotMetException()
    {
        var ex = VespaException.FromStatusCode(412, "condition failed");
        Assert.IsType<VespaConditionNotMetException>(ex);
        Assert.Equal(412, ex.StatusCode);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(504)]
    public void FromStatusCode_Timeout_ReturnsVespaTimeoutException(int statusCode)
    {
        var ex = VespaException.FromStatusCode(statusCode, "timeout");
        Assert.IsType<VespaTimeoutException>(ex);
        Assert.Equal(statusCode, ex.StatusCode);
    }

    [Fact]
    public void FromStatusCode_429_ReturnsVespaTooManyRequestsException()
    {
        // 429 is Vespa's standard backpressure signal — callers need a typed catch
        var ex = VespaException.FromStatusCode(429, "throttled");
        Assert.IsType<VespaTooManyRequestsException>(ex);
        Assert.Equal(429, ex.StatusCode);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void FromStatusCode_5xx_ReturnsVespaServerException(int statusCode)
    {
        var ex = VespaException.FromStatusCode(statusCode, "server error");
        Assert.IsType<VespaServerException>(ex);
        Assert.Equal(statusCode, ex.StatusCode);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(409)]
    public void FromStatusCode_Other4xx_ReturnsBaseVespaException(int statusCode)
    {
        var ex = VespaException.FromStatusCode(statusCode, "client error");
        Assert.IsType<VespaException>(ex);
        Assert.Equal(statusCode, ex.StatusCode);
    }

    [Fact]
    public void FromStatusCode_PreservesError()
    {
        var error = new VespaError { Code = 404, Message = "Not found" };
        var ex = VespaException.FromStatusCode(404, "msg", error);
        Assert.Same(error, ex.Error);
    }

    // ── Subtype properties ────────────────────────────────────────────────────

    [Fact]
    public void VespaNotFoundException_IsVespaException()
    {
        var ex = new VespaNotFoundException("not found");
        Assert.IsAssignableFrom<VespaException>(ex);
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public void VespaConditionNotMetException_IsVespaException()
    {
        var ex = new VespaConditionNotMetException("cond failed");
        Assert.IsAssignableFrom<VespaException>(ex);
        Assert.Equal(412, ex.StatusCode);
    }

    [Fact]
    public void VespaTimeoutException_DefaultStatusCode_Is504()
    {
        var ex = new VespaTimeoutException("timeout");
        Assert.Equal(504, ex.StatusCode);
    }

    [Fact]
    public void VespaServerException_DefaultStatusCode_Is500()
    {
        var ex = new VespaServerException("server error");
        Assert.Equal(500, ex.StatusCode);
    }

    // ── catch-by-base still works ─────────────────────────────────────────────

    [Fact]
    public void TypedExceptions_CanBeCaughtAsVespaException()
    {
        bool caught;
        try { throw new VespaConditionNotMetException("cond"); }
        catch (VespaException) { caught = true; }
        Assert.True(caught);
    }

    // ── inner exception fix ───────────────────────────────────────────────────

    [Fact]
    public void VespaException_InnerException_IsPreserved()
    {
        var inner = new InvalidOperationException("root cause");
        var ex = new VespaException("outer", inner);
        Assert.Same(inner, ex.InnerException);
    }

    #endregion
}
