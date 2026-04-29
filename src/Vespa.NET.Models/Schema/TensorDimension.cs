namespace Vespa.Models.Schema;

/// <summary>
/// Represents a dimension in a Vespa tensor type
/// </summary>
public sealed class TensorDimension
{
    /// <summary>
    /// Dimension name
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Size of the dimension (null for mapped/sparse dimensions)
    /// </summary>
    public int? Size { get; }

    /// <summary>
    /// Whether this is an indexed (dense) dimension
    /// </summary>
    public bool IsIndexed => Size.HasValue;

    /// <summary>
    /// Whether this is a mapped (sparse) dimension
    /// </summary>
    public bool IsMapped => !Size.HasValue;

    private TensorDimension(string name, int? size)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Size = size;
    }

    /// <summary>
    /// Create an indexed (dense) dimension with fixed size
    /// </summary>
    public static TensorDimension Indexed(string name, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        return new TensorDimension(name, size);
    }

    /// <summary>
    /// Create a mapped (sparse) dimension
    /// </summary>
    public static TensorDimension Mapped(string name)
    {
        return new TensorDimension(name, null);
    }

    /// <summary>
    /// Convert to Vespa schema format
    /// </summary>
    public override string ToString()
    {
        return IsIndexed ? $"{Name}[{Size}]" : $"{Name}{{}}";
    }
}
