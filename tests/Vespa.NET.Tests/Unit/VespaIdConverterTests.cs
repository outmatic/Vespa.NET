using System.Text.Json;
using Vespa.Models;
using Xunit;

namespace Vespa.Tests.Unit;

public class VespaIdConverterTests
{
    private readonly JsonSerializerOptions _options;

    public VespaIdConverterTests()
    {
        _options = new JsonSerializerOptions
        {
            Converters = { new VespaIdConverter() }
        };
    }

    [Theory]
    [InlineData("\"id:namespace:documenttype::doc1\"", "doc1")]
    [InlineData("\"id:test:testdoc::12345\"", "12345")]
    [InlineData("\"id:ns:type::my:id:with:colons\"", "my:id:with:colons")]
    [InlineData("\"just-an-id\"", "just-an-id")]
    [InlineData("\"\"", "")]
    [InlineData("null", null)]
    public void Read_ShortensVespaId_Correctly(string json, string? expected)
    {
        // Act
        var result = JsonSerializer.Deserialize<string>(json, _options);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Write_WritesStringValue_Correctly()
    {
        // Arrange
        var value = "test-id";

        // Act
        var json = JsonSerializer.Serialize(value, _options);

        // Assert
        Assert.Equal("\"test-id\"", json);
    }

    [Fact]
    public void Read_WithMultipleColons_ReturnsLastPartAfterDoubleColon()
    {
        // Arrange
        const string json = "\"id:ns:type::part1:part2:part3\"";

        // Act
        var result = JsonSerializer.Deserialize<string>(json, _options);

        // Assert
        Assert.Equal("part1:part2:part3", result);
    }

    [Theory]
    [InlineData("id:ns:type::doc1", "doc1")]
    [InlineData("id:ns:type::", "")]
    [InlineData("no-colon", "no-colon")]
    [InlineData(":trailing-colon", "trailing-colon")]
    [InlineData("leading-colon:", "")]
    public void GetShortId_HandlesVariousFormats(string input, string expected)
    {
        // Since GetShortId is private, we test it through deserialization
        var json = $"\"{input}\"";
        var result = JsonSerializer.Deserialize<string>(json, _options);
        Assert.Equal(expected, result);
    }
}
