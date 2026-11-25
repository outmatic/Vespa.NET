namespace Vespa.Query;

/// <summary>Optional Vespa annotations for a term matched by <c>contains</c>.</summary>
public sealed record YqlTermAnnotations
{
    /// <summary>Marks this term as a filter.</summary>
    public bool? Filter { get; init; }

    /// <summary>Identifies this term so other terms can refer to it.</summary>
    public int? TermId { get; init; }

    /// <summary>Connects this term to another term for proximity ranking.</summary>
    public YqlTermConnectivity? Connectivity { get; init; }

    /// <summary>Overrides stemming for this term.</summary>
    public bool? Stem { get; init; }

    /// <summary>Overrides case normalization for this term.</summary>
    public bool? NormalizeCase { get; init; }

    /// <summary>Overrides accent removal for this term.</summary>
    public bool? AccentDrop { get; init; }

    /// <summary>Overrides the use of position data for this term.</summary>
    public bool? UsePositionData { get; init; }

    /// <summary>Sets the term weight for ranking.</summary>
    public int? Weight { get; init; }

    /// <summary>Enables or disables prefix matching.</summary>
    public bool? Prefix { get; init; }

    /// <summary>Enables or disables suffix matching in streaming search.</summary>
    public bool? Suffix { get; init; }

    /// <summary>Enables or disables substring matching in streaming search.</summary>
    public bool? Substring { get; init; }

    /// <summary>Sets the term significance for text ranking features.</summary>
    public double? Significance { get; init; }

    /// <summary>Controls whether this term contributes to ranking.</summary>
    public bool? Ranked { get; init; }

    /// <summary>Sets the language used to process this term.</summary>
    public string? Language { get; init; }

    /// <summary>Enables or disables NFKC normalization.</summary>
    public bool? Nfkc { get; init; }

    /// <summary>Controls schema-driven term transformations such as stemming and normalization.</summary>
    public bool? ImplicitTransforms { get; init; }

    internal bool IsEmpty =>
        Filter is null && TermId is null && Connectivity is null && Stem is null &&
        NormalizeCase is null && AccentDrop is null && UsePositionData is null && Weight is null &&
        Prefix is null && Suffix is null && Substring is null && Significance is null &&
        Ranked is null && Language is null && Nfkc is null && ImplicitTransforms is null;
}

/// <summary>A weighted connection to another term in a Vespa query.</summary>
/// <param name="TargetTermId">The ID assigned to the other term.</param>
/// <param name="Weight">The connection weight used for proximity ranking.</param>
public readonly record struct YqlTermConnectivity(int TargetTermId, double Weight);
