using Vespa.Models.Schema;

namespace Vespa.Models.Attributes;

/// <summary>
/// Declares a constant tensor in the Vespa schema.
/// Generates <c>constant name { file: path type: tensorType }</c> at the schema level.
/// Set <see cref="SourcePath"/> to have the file automatically bundled in the application package.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class VespaConstantAttribute(string name, string file, string tensorType) : Attribute
{
    /// <summary>Constant name (e.g. "my_weights").</summary>
    public string Name { get; } = name;

    /// <summary>File path relative to the application package (e.g. "constants/my_weights.json").</summary>
    public string File { get; } = file;

    /// <summary>Tensor type string (e.g. "tensor&lt;float&gt;(x[3])").</summary>
    public string TensorType { get; } = tensorType;

    /// <summary>
    /// Absolute or relative path to the source file on disk.
    /// When set, <see cref="VespaSchemaBuilder"/> automatically includes this file
    /// in the application package ZIP at the path specified by <see cref="File"/>.
    /// </summary>
    public string? SourcePath { get; set; }
}
