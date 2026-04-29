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
}
