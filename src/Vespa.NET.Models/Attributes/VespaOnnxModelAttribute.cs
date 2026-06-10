using Vespa.Models.Schema;

namespace Vespa.Models.Attributes;

/// <summary>
/// Declares an ONNX model in the Vespa schema.
/// Generates <c>onnx-model name { file: path }</c> at the schema level.
/// Set <see cref="SourcePath"/> to have the file automatically bundled in the application package.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class VespaOnnxModelAttribute(string name, string file) : Attribute
{
    /// <summary>Model name in the schema (e.g. "my_model").</summary>
    public string Name { get; } = name;

    /// <summary>File path relative to the application package (e.g. "models/my_model.onnx").</summary>
    public string File { get; } = file;

    /// <summary>
    /// Absolute or relative path to the source file on disk.
    /// When set, <c>VespaSchemaBuilder</c> automatically includes this file
    /// in the application package ZIP at the path specified by <see cref="File"/>.
    /// </summary>
    public string? SourcePath { get; set; }
}
