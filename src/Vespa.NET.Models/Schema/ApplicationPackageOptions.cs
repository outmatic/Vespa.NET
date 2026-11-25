using System.Collections.Frozen;

namespace Vespa.Models.Schema;

/// <summary>
/// Options for generating a Vespa application package via <c>VespaSchemaBuilder</c>.
/// </summary>
public sealed record ApplicationPackageOptions
{
    private readonly IReadOnlyDictionary<string, string>? _documentSelections;

    internal IReadOnlyDictionary<Type, IReadOnlyList<RankProfileDefinition>>? RankProfiles { get; init; }

    /// <summary>
    /// Native keep selections keyed by document type. Values are copied when assigned.
    /// Selections control feed routing and enable garbage collection of non-matching documents.
    /// A target must belong to the package and must not also declare an attribute selection.
    /// Cannot be combined with <see cref="CustomServicesXml"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? DocumentSelections
    {
        get => _documentSelections;
        init => _documentSelections = value is null ? null :
            new Dictionary<string, string>(value, StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Number of redundant copies of each document. Default: 1.
    /// </summary>
    public int Redundancy { get; init; } = 1;

    /// <summary>
    /// Number of content nodes in the cluster. Default: 1.
    /// When greater than 1 a <c>hosts.xml</c> is generated alongside
    /// <c>services.xml</c> and every node gets a host alias <c>node1</c>…<c>nodeN</c>;
    /// set <see cref="HostNames"/> to map those aliases to real hostnames.
    /// </summary>
    public int NodeCount { get; init; } = 1;

    /// <summary>
    /// Hostnames written to the generated <c>hosts.xml</c> for multi-node packages
    /// (<see cref="NodeCount"/> &gt; 1). Must contain exactly <see cref="NodeCount"/>
    /// entries when set. When omitted, the aliases themselves (<c>node1</c>…<c>nodeN</c>)
    /// are used as hostnames — suitable for Docker-compose-style networks where
    /// container names resolve; override with real FQDNs for actual clusters.
    /// Ignored for single-node packages (no <c>hosts.xml</c> is generated).
    /// </summary>
    public IReadOnlyList<string>? HostNames { get; init; }

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
    /// Minimum Vespa server version declared in the generated <c>services.xml</c>.
    /// Supply a three-component version such as <c>8.640.27</c>.
    /// The config server checks it during deployment. Omit it to leave the requirement unset.
    /// For <see cref="CustomServicesXml"/>, declare the requirement directly in the XML.
    /// </summary>
    public Version? MinimumRequiredVespaVersion { get; init; }

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
