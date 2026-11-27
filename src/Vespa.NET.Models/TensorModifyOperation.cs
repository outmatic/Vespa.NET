namespace Vespa.Models;

/// <summary>Operation applied to each addressed cell in a tensor modify update.</summary>
public enum TensorModifyOperation
{
    /// <summary>Set the cell to the supplied value.</summary>
    Replace,

    /// <summary>Add the supplied value to the cell.</summary>
    Add,

    /// <summary>Multiply the cell by the supplied value.</summary>
    Multiply
}
