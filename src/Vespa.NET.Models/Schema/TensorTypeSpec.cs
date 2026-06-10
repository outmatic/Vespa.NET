namespace Vespa.Models.Schema;

/// <summary>
/// Type-safe builder for Vespa tensor type specifications
/// </summary>
public sealed class TensorTypeSpec
{
    private readonly TensorValueType _valueType;
    private readonly List<TensorDimension> _dimensions = new();

    private TensorTypeSpec(TensorValueType valueType)
    {
        _valueType = valueType;
    }

    /// <summary>
    /// Create a float tensor type (default)
    /// </summary>
    public static TensorTypeSpec Float() => new(TensorValueType.Float);

    /// <summary>
    /// Create a double tensor type
    /// </summary>
    public static TensorTypeSpec Double() => new(TensorValueType.Double);

    /// <summary>
    /// Create an int8 tensor type
    /// </summary>
    public static TensorTypeSpec Int8() => new(TensorValueType.Int8);

    /// <summary>
    /// Create a bfloat16 tensor type
    /// </summary>
    public static TensorTypeSpec BFloat16() => new(TensorValueType.BFloat16);

    /// <summary>
    /// Add an indexed (dense) dimension
    /// </summary>
    public TensorTypeSpec WithIndexedDimension(string name, int size)
    {
        _dimensions.Add(TensorDimension.Indexed(name, size));
        return this;
    }

    /// <summary>
    /// Add a mapped (sparse) dimension
    /// </summary>
    public TensorTypeSpec WithMappedDimension(string name)
    {
        _dimensions.Add(TensorDimension.Mapped(name));
        return this;
    }

    /// <summary>
    /// Get all dimensions
    /// </summary>
    public IReadOnlyList<TensorDimension> Dimensions => _dimensions.AsReadOnly();

    /// <summary>
    /// Get the value type
    /// </summary>
    public TensorValueType ValueType => _valueType;

    /// <summary>
    /// Convert to Vespa schema format: tensor&lt;float&gt;(x[384])
    /// </summary>
    public override string ToString()
    {
        if (_dimensions.Count == 0)
            throw new InvalidOperationException("Tensor must have at least one dimension");

        var valueTypeName = _valueType switch
        {
            TensorValueType.Float => "float",
            TensorValueType.Double => "double",
            TensorValueType.Int8 => "int8",
            TensorValueType.BFloat16 => "bfloat16",
            _ => throw new ArgumentOutOfRangeException(nameof(_valueType), _valueType, "Unknown tensor value type.")
        };

        var dimensionsStr = string.Join(",", _dimensions.Select(d => d.ToString()));
        return $"tensor<{valueTypeName}>({dimensionsStr})";
    }

    /// <summary>
    /// Parse a Vespa tensor type string into a TensorTypeSpec
    /// </summary>
    public static TensorTypeSpec Parse(string tensorType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tensorType);

        // Example: "tensor<float>(x[384])" or "tensor<float>(x{},y[2])"
        if (!tensorType.StartsWith("tensor", StringComparison.Ordinal))
            throw new FormatException($"Invalid tensor type format: {tensorType} (expected to start with \"tensor\").");

        // Extract value type
        var valueTypeStart = tensorType.IndexOf('<') + 1;
        var valueTypeEnd = tensorType.IndexOf('>');
        if (valueTypeStart <= 0 || valueTypeEnd <= valueTypeStart)
            throw new FormatException($"Invalid tensor type format: {tensorType}");

        var valueTypeStr = tensorType[valueTypeStart..valueTypeEnd];
        var valueType = valueTypeStr switch
        {
            "float" => TensorValueType.Float,
            "double" => TensorValueType.Double,
            "int8" => TensorValueType.Int8,
            "bfloat16" => TensorValueType.BFloat16,
            _ => throw new FormatException($"Unknown tensor value type: {valueTypeStr}")
        };

        var spec = new TensorTypeSpec(valueType);

        // Extract dimensions
        var dimensionsStart = tensorType.IndexOf('(') + 1;
        var dimensionsEnd = tensorType.LastIndexOf(')');
        if (dimensionsStart <= 0 || dimensionsEnd <= dimensionsStart)
            throw new FormatException($"Invalid tensor type format: {tensorType}");

        var dimensionsStr = tensorType[dimensionsStart..dimensionsEnd];
        var dimensionParts = dimensionsStr.Split(',', StringSplitOptions.TrimEntries);

        foreach (var dimStr in dimensionParts)
        {
            if (dimStr.Contains('['))
            {
                // Indexed dimension: x[384]
                var nameEnd = dimStr.IndexOf('[');
                var name = dimStr[..nameEnd];
                var sizeStr = dimStr[(nameEnd + 1)..^1]; // Remove '[' and ']'
                if (int.TryParse(sizeStr, out var size))
                {
                    spec.WithIndexedDimension(name, size);
                }
                else
                {
                    throw new FormatException($"Invalid dimension size: {sizeStr}");
                }
            }
            else if (dimStr.Contains('{'))
            {
                // Mapped dimension: x{}
                var nameEnd = dimStr.IndexOf('{');
                var name = dimStr[..nameEnd];
                spec.WithMappedDimension(name);
            }
            else
            {
                throw new FormatException($"Invalid dimension format: {dimStr}");
            }
        }

        return spec;
    }
}
