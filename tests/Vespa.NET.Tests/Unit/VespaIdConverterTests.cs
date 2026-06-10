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
    // Strings that are not full Vespa document IDs must pass through untouched
    [InlineData(":trailing-colon", ":trailing-colon")]
    [InlineData("leading-colon:", "leading-colon:")]
    public void GetShortId_HandlesVariousFormats(string input, string expected)
    {
        // Since GetShortId is private, we test it through deserialization
        var json = $"\"{input}\"";
        var result = JsonSerializer.Deserialize<string>(json, _options);
        Assert.Equal(expected, result);
    }

    [Theory]
    // User-specified part may itself contain "::" (id grammar: id:<ns>:<type>:<k/v>:<user>)
    [InlineData("id:ns:music::album::dark-side", "album::dark-side")]
    // Location selectors (g=/n=) occupy the key/value slot; user part keeps its colons
    [InlineData("id:ns:music:g=group1:doc:1", "doc:1")]
    [InlineData("id:ns:music:n=123:42", "42")]
    public void Read_LegalIdsWithSelectorsOrColons_ReturnsFullUserPart(string input, string expected)
    {
        var result = JsonSerializer.Deserialize<string>($"\"{input}\"", _options);
        Assert.Equal(expected, result);
    }

    [Theory]
    // Non-document hit IDs (e.g. grouping results) must never be shortened
    [InlineData("group:root:0")]
    [InlineData("a::b")]
    [InlineData("index:content/0/abc123")]
    public void Read_NonDocumentIds_PassThroughUnchanged(string input)
    {
        var result = JsonSerializer.Deserialize<string>($"\"{input}\"", _options);
        Assert.Equal(input, result);
    }
}
