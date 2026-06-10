using System.Text.Json;
using System.Text.Json.Serialization;
using Vespa.Models;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for VespaEmbeddingConverter — the simplified float[] converter.
/// The reader must consume exactly the tokens of the embedding value:
/// a desync corrupts every property parsed after the field.
/// </summary>
public class VespaEmbeddingConverterTests
{
    private sealed record Wrapper
    {
        [JsonPropertyName("embedding")]
        [JsonConverter(typeof(VespaEmbeddingConverter))]
        public float[]? Embedding { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private static Wrapper? Deserialize(string json) => JsonSerializer.Deserialize<Wrapper>(json);

    [Fact]
    public void Read_FlatArray_Parses()
    {
        var result = Deserialize("""{"embedding":[1.0,2.0,3.0],"name":"x"}""");
        Assert.Equal([1f, 2f, 3f], result!.Embedding);
        Assert.Equal("x", result.Name);
    }

    [Fact]
    public void Read_NestedArrays_FlattensWithoutDesync()
    {
        // Multi-dimensional short form — the old reader stopped at the first
        // inner EndArray and left the reader mid-value
        var result = Deserialize("""{"embedding":[[1.0,2.0],[3.0,4.0]],"name":"x"}""");
        Assert.Equal([1f, 2f, 3f, 4f], result!.Embedding);
        Assert.Equal("x", result.Name);
    }

    [Fact]
    public void Read_ValuesProperty_Parses()
    {
        var result = Deserialize("""{"embedding":{"type":"tensor<float>(x[2])","values":[1.0,2.0]},"name":"x"}""");
        Assert.Equal([1f, 2f], result!.Embedding);
        Assert.Equal("x", result.Name);
    }

    [Fact]
    public void Read_NestedValues_FlattensWithoutDesync()
    {
        var result = Deserialize("""{"embedding":{"values":[[1.0,2.0],[3.0,4.0]],"type":"t"},"name":"x"}""");
        Assert.Equal([1f, 2f, 3f, 4f], result!.Embedding);
        Assert.Equal("x", result.Name);
    }

    [Fact]
    public void Read_MappedCellsObject_ParsesWithoutDesync()
    {
        // Short form mapped cells: {"cells":{"a":1.0}} — the old reader walked into
        // the inner object and terminated on its EndObject
        var result = Deserialize("""{"embedding":{"type":"tensor(x{})","cells":{"a":1.0,"b":2.0}},"name":"x"}""");
        Assert.Equal([1f, 2f], result!.Embedding);
        Assert.Equal("x", result.Name);
    }

    [Fact]
    public void Read_LongFormCells_ExtractsValues()
    {
        var result = Deserialize("""{"embedding":{"cells":[{"address":{"x":"0"},"value":2.0},{"address":{"x":"1"},"value":3.0}]},"name":"x"}""");
        Assert.Equal([2f, 3f], result!.Embedding);
        Assert.Equal("x", result.Name);
    }

    [Fact]
    public void Read_Null_ReturnsNull()
    {
        var result = Deserialize("""{"embedding":null,"name":"x"}""");
        Assert.Null(result!.Embedding);
        Assert.Equal("x", result.Name);
    }

    [Fact]
    public void Write_RoundTripsAsFlatArray()
    {
        var json = JsonSerializer.Serialize(new Wrapper { Embedding = [1f, 2f] });
        Assert.Contains("[1,2]", json);
    }
}
