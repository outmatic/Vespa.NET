using System.Text.Json;
using Vespa.Models.Tensors;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for VespaTensorConverter covering all 5 tensor formats and 4 value types
/// Critical component: 5 formats × 4 types = 20 combinations + edge cases
/// </summary>
public class VespaTensorConverterTests
{
    private readonly JsonSerializerOptions _jsonOptions;

    public VespaTensorConverterTests()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new VespaTensorConverter() }
        };
    }

    #region Vespa documented JSON forms (docs.vespa.ai/en/reference/document-json-format.html)

    [Fact]
    public void Read_CellsArrayOfObjects_LongForm_DeserializesAsCells()
    {
        // Default document/v1 rendering (no format.tensors=short)
        var json = """{"type":"tensor(x{})","cells":[{"address":{"x":"a"},"value":2.0},{"address":{"x":"b"},"value":3.0}]}""";

        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        Assert.NotNull(tensor);
        Assert.Equal(TensorFormat.Verbose, tensor.Format);
        Assert.NotNull(tensor.Cells);
        Assert.Equal(2, tensor.Cells.Count);
        Assert.Equal("a", tensor.Cells[0].Address["x"]);
        Assert.Equal(2.0, tensor.Cells[0].Value);
        Assert.Equal("b", tensor.Cells[1].Address["x"]);
        Assert.Equal(3.0, tensor.Cells[1].Value);
    }

    [Fact]
    public void Read_CellsObject_ShortFormMapped_DeserializesAsMapped()
    {
        // Short form for mapped tensors: {"cells":{"a":2.0,"b":3.0}}
        var json = """{"type":"tensor(x{})","cells":{"a":2.0,"b":3.0}}""";

        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        Assert.NotNull(tensor);
        Assert.Equal(TensorFormat.MappedSingle, tensor.Format);
        var values = tensor.GetMappedValues<double>();
        Assert.NotNull(values);
        Assert.Equal(2.0, values["a"]);
        Assert.Equal(3.0, values["b"]);
    }

    [Fact]
    public void Read_NestedValues_MultiDimIndexed_FlattensRowMajor()
    {
        // Short form for multi-dimensional indexed tensors: nested arrays
        var json = """{"type":"tensor<float>(x[2],y[2])","values":[[2.0,3.0],[5.0,7.0]]}""";

        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        Assert.NotNull(tensor);
        Assert.Equal(TensorFormat.IndexedDense, tensor.Format);
        var values = tensor.GetDenseValues<float>();
        Assert.NotNull(values);
        Assert.Equal(new[] { 2.0f, 3.0f, 5.0f, 7.0f }, values);
    }

    [Fact]
    public void Read_BlocksObject_ShortFormMixed_DeserializesAsMixedSingleSparse()
    {
        // Short form for mixed tensors with a single mapped dimension
        var json = """{"type":"tensor(x{},y[2])","blocks":{"a":[1.0,2.0],"b":[3.0,4.0]}}""";

        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        Assert.NotNull(tensor);
        Assert.Equal(TensorFormat.MixedSingleSparse, tensor.Format);
        var values = tensor.GetMixedSingleSparse<double>();
        Assert.NotNull(values);
        Assert.Equal(new[] { 1.0, 2.0 }, values["a"]);
        Assert.Equal(new[] { 3.0, 4.0 }, values["b"]);
    }

    #endregion

    #region IndexedDense Format Tests (12 tests)

    [Fact]
    public void Read_IndexedDense_DefaultToDouble_DeserializesCorrectly()
    {
        // Arrange - Without type spec, defaults to double
        var json = "[1.0, 2.0, 3.0]";

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        Assert.Equal(TensorFormat.IndexedDense, tensor.Format);
        var values = tensor.GetDenseValues<double>();
        Assert.NotNull(values);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, values);
    }

    [Fact]
    public void Read_IndexedDense_Double_DeserializesCorrectly()
    {
        // Arrange
        var json = "[1.5, 2.5, 3.5]";

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        Assert.Equal(TensorFormat.IndexedDense, tensor.Format);
        var values = tensor.GetDenseValues<double>();
        Assert.NotNull(values);
        Assert.Equal(new[] { 1.5, 2.5, 3.5 }, values);
    }

    [Fact]
    public void Write_IndexedDense_Float_SerializesCorrectly()
    {
        // Arrange
        var tensor = VespaTensor.FromDenseValues([1.0f, 2.0f, 3.0f]);

        // Act
        var json = JsonSerializer.Serialize(tensor, _jsonOptions);

        // Assert
        Assert.Equal("[1,2,3]", json);
    }

    [Fact]
    public void Write_IndexedDense_Double_SerializesCorrectly()
    {
        // Arrange
        var tensor = VespaTensor.FromDenseValues([1.5, 2.5, 3.5]);

        // Act
        var json = JsonSerializer.Serialize(tensor, _jsonOptions);

        // Assert
        Assert.Equal("[1.5,2.5,3.5]", json);
    }

    [Fact]
    public void RoundTrip_IndexedDense_Float_PreservesData()
    {
        // Arrange
        var original = VespaTensor.FromDenseValues([1.0f, 2.0f, 3.0f]);

        // Act
        var json = JsonSerializer.Serialize(original, _jsonOptions);
        var deserialized = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(deserialized);
        // Note: Without type spec, deserializes as double by default
        // So we check that values are equivalent as double
        var originalAsDouble = new[] { 1.0, 2.0, 3.0 };
        Assert.Equal(originalAsDouble, deserialized.GetDenseValues<double>());
    }

    [Fact]
    public void RoundTrip_IndexedDense_Double_PreservesData()
    {
        // Arrange
        var original = VespaTensor.FromDenseValues([1.5, 2.5, 3.5]);

        // Act
        var json = JsonSerializer.Serialize(original, _jsonOptions);
        var deserialized = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.GetDenseValues<double>(), deserialized.GetDenseValues<double>());
    }

    [Fact]
    public void Read_IndexedDense_EmptyArray_CreatesEmptyTensor()
    {
        // Arrange
        var json = "[]";

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        Assert.Equal(TensorFormat.IndexedDense, tensor.Format);
        var values = tensor.GetDenseValues<double>();
        Assert.NotNull(values);
        Assert.Empty(values);
    }

    [Fact]
    public void Read_IndexedDense_SingleValue_DeserializesCorrectly()
    {
        // Arrange
        var json = "[42.0]";

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        var values = tensor.GetDenseValues<double>();
        Assert.NotNull(values);
        Assert.Single(values);
        Assert.Equal(42.0, values[0]);
    }

    [Fact]
    public void Read_IndexedDense_LargeArray_DeserializesCorrectly()
    {
        // Arrange - Uses double by default
        var expected = Enumerable.Range(0, 10000).Select(i => (double)i).ToArray();
        var json = JsonSerializer.Serialize(expected);

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        var values = tensor.GetDenseValues<double>();
        Assert.NotNull(values);
        Assert.Equal(10000, values.Length);
        Assert.Equal(expected, values);
    }

    [Fact]
    public void Read_IndexedDense_Null_ReturnsNull()
    {
        // Arrange
        var json = "null";

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.Null(tensor);
    }

    #endregion

    #region MappedSingle Format Tests (12 tests)

    [Fact]
    public void Read_MappedSingle_DefaultToDouble_DeserializesCorrectly()
    {
        // Arrange - Without type spec, defaults to double
        var json = @"{""a"": 1.0, ""b"": 2.0, ""c"": 3.0}";

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        Assert.Equal(TensorFormat.MappedSingle, tensor.Format);
        var values = tensor.GetMappedValues<double>();
        Assert.NotNull(values);
        Assert.Equal(3, values.Count);
        Assert.Equal(1.0, values["a"]);
        Assert.Equal(2.0, values["b"]);
        Assert.Equal(3.0, values["c"]);
    }

    [Fact]
    public void Read_MappedSingle_Double_DeserializesCorrectly()
    {
        // Arrange
        var json = @"{""x"": 1.5, ""y"": 2.5}";

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        Assert.Equal(TensorFormat.MappedSingle, tensor.Format);
        var values = tensor.GetMappedValues<double>();
        Assert.NotNull(values);
        Assert.Equal(2, values.Count);
        Assert.Equal(1.5, values["x"]);
        Assert.Equal(2.5, values["y"]);
    }

    [Fact]
    public void Write_MappedSingle_Float_SerializesCorrectly()
    {
        // Arrange
        var values = new Dictionary<string, float>
        {
            ["a"] = 1.0f,
            ["b"] = 2.0f
        };
        var tensor = VespaTensor.FromMappedValues(values);

        // Act
        var json = JsonSerializer.Serialize(tensor, _jsonOptions);
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, float>>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(1.0f, deserialized["a"]);
        Assert.Equal(2.0f, deserialized["b"]);
    }

    [Fact]
    public void Write_MappedSingle_Double_SerializesCorrectly()
    {
        // Arrange
        var values = new Dictionary<string, double>
        {
            ["x"] = 1.5,
            ["y"] = 2.5
        };
        var tensor = VespaTensor.FromMappedValues(values);

        // Act
        var json = JsonSerializer.Serialize(tensor, _jsonOptions);
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, double>>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(1.5, deserialized["x"]);
        Assert.Equal(2.5, deserialized["y"]);
    }

    [Fact]
    public void RoundTrip_MappedSingle_Float_PreservesData()
    {
        // Arrange
        var original = new Dictionary<string, float>
        {
            ["a"] = 1.0f,
            ["b"] = 2.0f,
            ["c"] = 3.0f
        };
        var tensor = VespaTensor.FromMappedValues(original);

        // Act
        var json = JsonSerializer.Serialize(tensor, _jsonOptions);
        var deserialized = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(deserialized);
        // Note: Without type spec, deserializes as double by default
        var values = deserialized.GetMappedValues<double>();
        Assert.NotNull(values);
        Assert.Equal(3, values.Count);
        Assert.Equal(1.0, values["a"]);
        Assert.Equal(2.0, values["b"]);
        Assert.Equal(3.0, values["c"]);
    }

    [Fact]
    public void RoundTrip_MappedSingle_Double_PreservesData()
    {
        // Arrange
        var original = new Dictionary<string, double>
        {
            ["x"] = 1.5,
            ["y"] = 2.5
        };
        var tensor = VespaTensor.FromMappedValues(original);

        // Act
        var json = JsonSerializer.Serialize(tensor, _jsonOptions);
        var deserialized = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(deserialized);
        var values = deserialized.GetMappedValues<double>();
        Assert.Equal(original, values);
    }

    [Fact]
    public void Read_MappedSingle_EmptyDictionary_CreatesEmptyTensor()
    {
        // Arrange - Empty object is parsed as empty dense array by the converter
        var json = "{}";

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        // Note: Empty {} is treated as IndexedDense format with empty array
        Assert.Equal(TensorFormat.IndexedDense, tensor.Format);
        var values = tensor.GetDenseValues<double>();
        Assert.NotNull(values);
        Assert.Empty(values);
    }

    [Fact]
    public void Read_MappedSingle_SingleEntry_DeserializesCorrectly()
    {
        // Arrange
        var json = @"{""single"": 42.0}";

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        var values = tensor.GetMappedValues<double>();
        Assert.NotNull(values);
        Assert.Single(values);
        Assert.Equal(42.0, values["single"]);
    }

    [Fact]
    public void Read_MappedSingle_SpecialKeyNames_DeserializesCorrectly()
    {
        // Arrange
        var json = @"{""key with spaces"": 1.0, ""key-with-dashes"": 2.0, ""key_with_underscores"": 3.0}";

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        var values = tensor.GetMappedValues<double>();
        Assert.NotNull(values);
        Assert.Equal(3, values.Count);
        Assert.Equal(1.0, values["key with spaces"]);
        Assert.Equal(2.0, values["key-with-dashes"]);
        Assert.Equal(3.0, values["key_with_underscores"]);
    }

    [Fact]
    public void Read_MappedSingle_LargeDictionary_DeserializesCorrectly()
    {
        // Arrange
        var expected = Enumerable.Range(0, 1000)
            .ToDictionary(i => $"key{i}", i => (double)i);
        var json = JsonSerializer.Serialize(expected);

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        var values = tensor.GetMappedValues<double>();
        Assert.NotNull(values);
        Assert.Equal(1000, values.Count);
        Assert.Equal(expected, values);
    }

    #endregion

    #region MixedSingleSparse Format Tests (12 tests)

    [Fact]
    public void Read_MixedSingleSparse_DefaultToDouble_DeserializesCorrectly()
    {
        // Arrange - Without type spec, defaults to double
        var json = @"{""x1"": [1.0, 2.0], ""x2"": [3.0, 4.0, 5.0]}";

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        Assert.Equal(TensorFormat.MixedSingleSparse, tensor.Format);
        var values = tensor.GetMixedSingleSparse<double>();
        Assert.NotNull(values);
        Assert.Equal(2, values.Count);
        Assert.Equal(new[] { 1.0, 2.0 }, values["x1"]);
        Assert.Equal(new[] { 3.0, 4.0, 5.0 }, values["x2"]);
    }

    [Fact]
    public void Read_MixedSingleSparse_Double_DeserializesCorrectly()
    {
        // Arrange
        var json = @"{""dim1"": [1.5, 2.5], ""dim2"": [3.5]}";

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        Assert.Equal(TensorFormat.MixedSingleSparse, tensor.Format);
        var values = tensor.GetMixedSingleSparse<double>();
        Assert.NotNull(values);
        Assert.Equal(2, values.Count);
        Assert.Equal(new[] { 1.5, 2.5 }, values["dim1"]);
        Assert.Equal(new[] { 3.5 }, values["dim2"]);
    }

    [Fact]
    public void Write_MixedSingleSparse_Float_SerializesCorrectly()
    {
        // Arrange
        var values = new Dictionary<string, float[]>
        {
            ["x1"] = [1.0f, 2.0f],
            ["x2"] = [3.0f]
        };
        var tensor = VespaTensor.FromMixedSingleSparse(values);

        // Act
        var json = JsonSerializer.Serialize(tensor, _jsonOptions);
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, float[]>>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(new[] { 1.0f, 2.0f }, deserialized["x1"]);
        Assert.Equal(new[] { 3.0f }, deserialized["x2"]);
    }

    [Fact]
    public void Write_MixedSingleSparse_Double_SerializesCorrectly()
    {
        // Arrange
        var values = new Dictionary<string, double[]>
        {
            ["dim1"] = [1.5, 2.5],
            ["dim2"] = [3.5]
        };
        var tensor = VespaTensor.FromMixedSingleSparse(values);

        // Act
        var json = JsonSerializer.Serialize(tensor, _jsonOptions);
        var deserialized = JsonSerializer.Deserialize<Dictionary<string, double[]>>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(new[] { 1.5, 2.5 }, deserialized["dim1"]);
        Assert.Equal(new[] { 3.5 }, deserialized["dim2"]);
    }

    [Fact]
    public void RoundTrip_MixedSingleSparse_Float_PreservesData()
    {
        // Arrange
        var original = new Dictionary<string, float[]>
        {
            ["x1"] = [1.0f, 2.0f],
            ["x2"] = [3.0f, 4.0f, 5.0f]
        };
        var tensor = VespaTensor.FromMixedSingleSparse(original);

        // Act
        var json = JsonSerializer.Serialize(tensor, _jsonOptions);
        var deserialized = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(deserialized);
        // Note: Without type spec, deserializes as double by default
        var values = deserialized.GetMixedSingleSparse<double>();
        Assert.NotNull(values);
        Assert.Equal(2, values.Count);
        Assert.Equal(new[] { 1.0, 2.0 }, values["x1"]);
        Assert.Equal(new[] { 3.0, 4.0, 5.0 }, values["x2"]);
    }

    [Fact]
    public void RoundTrip_MixedSingleSparse_Double_PreservesData()
    {
        // Arrange
        var original = new Dictionary<string, double[]>
        {
            ["dim1"] = [1.5, 2.5],
            ["dim2"] = [3.5]
        };
        var tensor = VespaTensor.FromMixedSingleSparse(original);

        // Act
        var json = JsonSerializer.Serialize(tensor, _jsonOptions);
        var deserialized = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(deserialized);
        var values = deserialized.GetMixedSingleSparse<double>();
        Assert.NotNull(values);
        Assert.Equal(original.Count, values.Count);
        Assert.Equal(original["dim1"], values["dim1"]);
        Assert.Equal(original["dim2"], values["dim2"]);
    }

    [Fact]
    public void Read_MixedSingleSparse_EmptyArrays_DeserializesCorrectly()
    {
        // Arrange
        var json = @"{""empty1"": [], ""empty2"": []}";

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        var values = tensor.GetMixedSingleSparse<double>();
        Assert.NotNull(values);
        Assert.Equal(2, values.Count);
        Assert.Empty(values["empty1"]);
        Assert.Empty(values["empty2"]);
    }

    [Fact]
    public void Read_MixedSingleSparse_DifferentArrayLengths_DeserializesCorrectly()
    {
        // Arrange
        var json = @"{""short"": [1.0], ""medium"": [1.0, 2.0], ""long"": [1.0, 2.0, 3.0, 4.0]}";

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        var values = tensor.GetMixedSingleSparse<double>();
        Assert.NotNull(values);
        Assert.Equal(3, values.Count);
        Assert.Single(values["short"]);
        Assert.Equal(2, values["medium"].Length);
        Assert.Equal(4, values["long"].Length);
    }

    [Fact]
    public void Read_MixedSingleSparse_LargeSparseTensor_DeserializesCorrectly()
    {
        // Arrange
        var expected = Enumerable.Range(0, 100)
            .ToDictionary(
                i => $"dim{i}",
                i => Enumerable.Range(0, 10).Select(j => (double)(i * 10 + j)).ToArray()
            );
        var json = JsonSerializer.Serialize(expected);

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        var values = tensor.GetMixedSingleSparse<double>();
        Assert.NotNull(values);
        Assert.Equal(100, values.Count);
        foreach (var kvp in expected)
        {
            Assert.Equal(kvp.Value, values[kvp.Key]);
        }
    }

    [Fact]
    public void Read_MixedSingleSparse_EmptyObject_CreatesEmptyTensor()
    {
        // Arrange - Empty object is parsed as empty dense array by the converter
        var json = "{}";

        // Act
        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        // Assert
        Assert.NotNull(tensor);
        // Note: Empty {} is treated as IndexedDense format with empty array
        Assert.Equal(TensorFormat.IndexedDense, tensor.Format);
        var values = tensor.GetDenseValues<double>();
        Assert.NotNull(values);
        Assert.Empty(values);
    }

    #endregion

    #region Edge Cases and Error Handling (8 tests)

    [Fact]
    public void Read_InvalidJson_ThrowsJsonException()
    {
        // Arrange
        var json = "[1, 2, 3"; // Missing closing bracket

        // Act & Assert
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions));
    }

    [Fact]
    public void Read_UnexpectedTokenType_ThrowsJsonException()
    {
        // Arrange
        var json = "\"this is a string\""; // Strings are not valid tensor format

        // Act & Assert
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions));

        Assert.Contains("Unexpected token", ex.Message);
    }

    [Fact]
    public void DetectValueType_WithTypeSpec_Float_ReturnsFloatType()
    {
        // Arrange
        var typeSpec = "tensor<float>(x[384])";

        // Act
        var result = VespaTensor.DetectValueType(typeSpec);

        // Assert
        Assert.Equal(typeof(float), result);
    }

    [Fact]
    public void DetectValueType_WithTypeSpec_Double_ReturnsDoubleType()
    {
        // Arrange
        var typeSpec = "tensor<double>(x[128])";

        // Act
        var result = VespaTensor.DetectValueType(typeSpec);

        // Assert
        Assert.Equal(typeof(double), result);
    }

    [Fact]
    public void DetectValueType_WithTypeSpec_Int8_ReturnsSByteType()
    {
        // Arrange
        var typeSpec = "tensor<int8>(x[256])";

        // Act
        var result = VespaTensor.DetectValueType(typeSpec);

        // Assert
        Assert.Equal(typeof(sbyte), result);
    }

    [Fact]
    public void DetectValueType_WithTypeSpec_BFloat16_ReturnsFloatType()
    {
        // bfloat16 has float32 range (±3.4e38); System.Half saturates at 65504,
        // so float is the lossless in-memory representation.
        var typeSpec = "tensor<bfloat16>(x[512])";

        var result = VespaTensor.DetectValueType(typeSpec);

        Assert.Equal(typeof(float), result);
    }

    [Fact]
    public void Read_Int8Values_RenderedWithDecimals_Parses()
    {
        // Vespa renders tensor cell values as doubles (1.0), which GetSByte() rejects
        var json = """{"type":"tensor<int8>(x[3])","values":[1.0,2.0,3.0]}""";

        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        Assert.NotNull(tensor);
        Assert.Equal(new sbyte[] { 1, 2, 3 }, tensor.GetDenseValues<sbyte>());
    }

    [Fact]
    public void Read_Int8Cells_RenderedWithDecimals_Parses()
    {
        var json = """{"type":"tensor<int8>(x{})","cells":[{"address":{"x":"a"},"value":1.0}]}""";

        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        Assert.NotNull(tensor);
        Assert.Equal((sbyte)1, tensor.Cells![0].Value);
    }

    [Fact]
    public void Read_BFloat16_LargeMagnitude_DoesNotSaturate()
    {
        var json = """{"type":"tensor<bfloat16>(x[2])","values":[100000.0,1.0]}""";

        var tensor = JsonSerializer.Deserialize<VespaTensor>(json, _jsonOptions);

        Assert.NotNull(tensor);
        var values = tensor.GetDenseValues<float>();
        Assert.NotNull(values);
        Assert.Equal(100000f, values[0]);
    }

    [Fact]
    public void DetectValueType_WithNullTypeSpec_ReturnsDoubleType()
    {
        // Arrange
        string? typeSpec = null;

        // Act
        var result = VespaTensor.DetectValueType(typeSpec);

        // Assert
        Assert.Equal(typeof(double), result); // Default to double
    }

    [Fact]
    public void DetectValueType_WithUnknownTypeSpec_ReturnsDoubleType()
    {
        // Arrange
        var typeSpec = "tensor<unknown>(x[100])";

        // Act
        var result = VespaTensor.DetectValueType(typeSpec);

        // Assert
        Assert.Equal(typeof(double), result); // Fallback to double
    }

    #endregion
}
