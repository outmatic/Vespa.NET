using Vespa.Models.Tensors;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for VespaTensor class methods and properties
/// </summary>
public class VespaTensorTests
{
    #region Factory Methods Tests

    [Fact]
    public void FromDenseValues_Float_CreatesTensorCorrectly()
    {
        // Arrange
        var values = new[] { 1.0f, 2.0f, 3.0f };

        // Act
        var tensor = VespaTensor.FromDenseValues(values);

        // Assert
        Assert.NotNull(tensor);
        Assert.Equal(TensorFormat.IndexedDense, tensor.Format);
        Assert.Equal(values, tensor.GetDenseValues<float>());
    }

    [Fact]
    public void FromDenseValues_Double_CreatesTensorCorrectly()
    {
        // Arrange
        var values = new[] { 1.5, 2.5, 3.5 };

        // Act
        var tensor = VespaTensor.FromDenseValues(values);

        // Assert
        Assert.NotNull(tensor);
        Assert.Equal(TensorFormat.IndexedDense, tensor.Format);
        Assert.Equal(values, tensor.GetDenseValues<double>());
    }

    [Fact]
    public void FromDenseValues_WithTypeSpec_SetsTensorType()
    {
        // Arrange
        var values = new[] { 1.0f, 2.0f };
        var typeSpec = "tensor<float>(x[2])";

        // Act
        var tensor = VespaTensor.FromDenseValues(values, typeSpec);

        // Assert
        Assert.Equal(typeSpec, tensor.Type);
    }

    [Fact]
    public void FromMappedValues_CreatesTensorCorrectly()
    {
        // Arrange
        var values = new Dictionary<string, double>
        {
            ["a"] = 1.0,
            ["b"] = 2.0
        };

        // Act
        var tensor = VespaTensor.FromMappedValues(values);

        // Assert
        Assert.NotNull(tensor);
        Assert.Equal(TensorFormat.MappedSingle, tensor.Format);
        Assert.Equal(values, tensor.GetMappedValues<double>());
    }

    [Fact]
    public void FromMixedSingleSparse_CreatesTensorCorrectly()
    {
        // Arrange
        var values = new Dictionary<string, double[]>
        {
            ["x1"] = [1.0, 2.0],
            ["x2"] = [3.0]
        };

        // Act
        var tensor = VespaTensor.FromMixedSingleSparse(values);

        // Assert
        Assert.NotNull(tensor);
        Assert.Equal(TensorFormat.MixedSingleSparse, tensor.Format);
        var result = tensor.GetMixedSingleSparse<double>();
        Assert.NotNull(result);
        Assert.Equal(values.Count, result.Count);
        Assert.Equal(values["x1"], result["x1"]);
        Assert.Equal(values["x2"], result["x2"]);
    }

    [Fact]
    public void GetDenseValues_ZeroCopy_ReturnsOriginalArray()
    {
        // Arrange
        var original = new[] { 1.0f, 2.0f, 3.0f };
        var tensor = VespaTensor.FromDenseValues(original);

        // Act
        var retrieved = tensor.GetDenseValues<float>();

        // Assert
        Assert.Same(original, retrieved); // Should be the same instance (zero-copy)
    }

    [Fact]
    public void SetDenseValues_UpdatesInternalStorage()
    {
        // Arrange
        var tensor = new VespaTensor();
        var values = new[] { 1.0, 2.0, 3.0 };

        // Act
        tensor.SetDenseValues(values);
        tensor.Format = TensorFormat.IndexedDense;

        // Assert
        Assert.Equal(values, tensor.GetDenseValues<double>());
    }

    [Fact]
    public void GetMappedValues_ReturnsCorrectDictionary()
    {
        // Arrange
        var values = new Dictionary<string, float>
        {
            ["key1"] = 1.0f,
            ["key2"] = 2.0f
        };
        var tensor = VespaTensor.FromMappedValues(values);

        // Act
        var retrieved = tensor.GetMappedValues<float>();

        // Assert
        Assert.Equal(values, retrieved);
    }

    #endregion

    #region Numeric conversion on element-type mismatch

    [Fact]
    public void GetDenseValues_ElementTypeMismatch_ConvertsNumerically()
    {
        // Previously returned null (`as T[]`) — now converts
        var tensor = VespaTensor.FromDenseValues([1.5, 2.5]);

        var asFloat = tensor.GetDenseValues<float>();

        Assert.NotNull(asFloat);
        Assert.Equal(new[] { 1.5f, 2.5f }, asFloat);
    }

    [Fact]
    public void GetDenseValues_SByteToDouble_Converts()
    {
        var tensor = VespaTensor.FromDenseValues<sbyte>([1, -2, 3]);

        Assert.Equal(new[] { 1.0, -2.0, 3.0 }, tensor.GetDenseValues<double>());
    }

    [Fact]
    public void GetDenseValues_OutOfRange_ThrowsOverflow()
    {
        var tensor = VespaTensor.FromDenseValues([1000.0]);

        Assert.Throws<OverflowException>(() => tensor.GetDenseValues<sbyte>());
    }

    [Fact]
    public void GetDenseValues_Null_ReturnsNull()
    {
        var tensor = new VespaTensor();

        Assert.Null(tensor.GetDenseValues<double>());
    }

    [Fact]
    public void GetMappedValues_ElementTypeMismatch_ConvertsNumerically()
    {
        var tensor = VespaTensor.FromMappedValues(new Dictionary<string, float> { ["a"] = 1.0f });

        var asDouble = tensor.GetMappedValues<double>();

        Assert.NotNull(asDouble);
        Assert.Equal(1.0, asDouble["a"]);
    }

    [Fact]
    public void GetMixedSingleSparse_ElementTypeMismatch_ConvertsNumerically()
    {
        var tensor = VespaTensor.FromMixedSingleSparse(new Dictionary<string, float[]> { ["x1"] = [1.0f, 2.0f] });

        var asDouble = tensor.GetMixedSingleSparse<double>();

        Assert.NotNull(asDouble);
        Assert.Equal(new[] { 1.0, 2.0 }, asDouble["x1"]);
    }

    [Fact]
    public void TensorBlock_GetValues_ElementTypeMismatch_ConvertsNumerically()
    {
        var block = new TensorBlock();
        block.SetValues([1.0f, 2.0f]);

        Assert.Equal(new[] { 1.0, 2.0 }, block.GetValues<double>());
    }

    [Fact]
    public void GetMappedValues_UnknownStorageType_ThrowsInvalidCast()
    {
        var tensor = new VespaTensor();
        tensor.SetMappedValues(new Dictionary<string, int> { ["a"] = 1 });

        var ex = Assert.Throws<InvalidCastException>(() => tensor.GetMappedValues<double>());
        Assert.Contains("cannot be converted", ex.Message);
    }

    #endregion
}
