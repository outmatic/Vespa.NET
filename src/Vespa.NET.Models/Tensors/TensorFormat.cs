namespace Vespa.Models.Tensors;

/// <summary>
/// Represents the format of a Vespa tensor in JSON
/// </summary>
public enum TensorFormat
{
    /// <summary>
    /// Dense indexed tensor: [1.0, 2.0, 3.0]
    /// Used for: tensor&lt;float&gt;(x[3])
    /// </summary>
    IndexedDense,

    /// <summary>
    /// Single mapped dimension: {"a": 1.0, "b": 2.0}
    /// Used for: tensor&lt;float&gt;(x{})
    /// </summary>
    MappedSingle,

    /// <summary>
    /// Mixed tensor with single sparse dimension: {"x1": [1.0, 2.0], "x2": [3.0, 4.0]}
    /// Used for: tensor&lt;float&gt;(x{}, y[2])
    /// </summary>
    MixedSingleSparse,

    /// <summary>
    /// Mixed tensor with multiple sparse dimensions: {"blocks": [{"address": {...}, "values": [...]}]}
    /// Used for: tensor&lt;float&gt;(x{}, y{}, z[2])
    /// </summary>
    MixedMultiSparse,

    /// <summary>
    /// Verbose format: [{"address": {"x": "a"}, "value": 1.0}]
    /// Universal format that works for all tensor types
    /// </summary>
    Verbose
}
