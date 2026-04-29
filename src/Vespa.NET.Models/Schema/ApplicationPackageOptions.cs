namespace Vespa.Models.Schema;

/// <summary>
/// Options for generating a Vespa application package via <see cref="VespaSchemaBuilder"/>.
/// </summary>
public sealed record ApplicationPackageOptions
{
    /// <summary>
    /// Number of redundant copies of each document. Default: 1.
    /// </summary>
    public int Redundancy { get; init; } = 1;

    /// <summary>
    /// Number of content nodes in the cluster. Default: 1.
    /// </summary>
    public int NodeCount { get; init; } = 1;

    /// <summary>
    /// Additional files to include in the application package ZIP.
    /// Keys are ZIP entry paths (e.g. <c>"models/my_model.onnx"</c>),
    /// values are absolute or relative paths on disk.
    /// Files are streamed into the ZIP without loading entirely into memory.
    /// </summary>
    /// <remarks>
    /// Prefer setting <see cref="Vespa.Models.Attributes.VespaOnnxModelAttribute.SourcePath"/>
    /// and <see cref="Vespa.Models.Attributes.VespaConstantAttribute.SourcePath"/>
    /// for automatic inclusion. Use this for files not declared via attributes.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? AdditionalFiles { get; init; }

    /// <summary>
    /// When set, this XML string is used as <c>services.xml</c> verbatim,
    /// bypassing the auto-generated template entirely.
    /// This is an escape hatch for configurations that cannot be expressed
    /// via the other options on this record.
    /// </summary>
    public string? CustomServicesXml { get; init; }

    /// <summary>
    /// Additional raw XML fragments to inject inside the <c>&lt;container&gt;</c> element
    /// of the generated <c>services.xml</c>.
    /// Keys are identifiers (for documentation only), values are raw XML strings.
    /// Ignored when <see cref="CustomServicesXml"/> is set.
    /// </summary>
    /// <example>
    /// <code>
    /// ContainerOptions = new Dictionary&lt;string, string&gt;
    /// {
    ///     ["custom-handler"] = """&lt;handler id="com.example.MyHandler" bundle="my-bundle"/&gt;"""
    /// }
    /// </code>
    /// </example>
    public IReadOnlyDictionary<string, string>? ContainerOptions { get; init; }

    /// <summary>
    /// Additional raw XML fragments to inject inside the <c>&lt;content&gt;</c> element
    /// of the generated <c>services.xml</c>, after the <c>&lt;nodes&gt;</c> block.
    /// Keys are identifiers (for documentation only), values are raw XML strings.
    /// Ignored when <see cref="CustomServicesXml"/> is set.
    /// </summary>
    /// <example>
    /// <code>
    /// ContentOptions = new Dictionary&lt;string, string&gt;
    /// {
    ///     ["tuning"] = """
    ///         &lt;tuning&gt;
    ///             &lt;dispatch&gt;
    ///                 &lt;max-hits-per-partition&gt;1000&lt;/max-hits-per-partition&gt;
    ///             &lt;/dispatch&gt;
    ///         &lt;/tuning&gt;
    ///     """
    /// }
    /// </code>
    /// </example>
    public IReadOnlyDictionary<string, string>? ContentOptions { get; init; }
}
