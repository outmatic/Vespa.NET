namespace Vespa.Models.Attributes;

/// <summary>
/// Defines a named rank-profile function in the Vespa schema. Apply to the document
/// class, once per function. Generates
/// <c>function signature { expression: … }</c> inside the target rank profile.
/// </summary>
/// <example>
/// <code>
/// [VespaRankProfile("myrank", FirstPhase = "freshness")]
/// [VespaRankFunction("freshness", "now - attribute(timestamp)")]
/// [VespaRankFunction("myScore(a,b)", "a * b", Profile = "myrank")]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class VespaRankFunctionAttribute : Attribute
{
    /// <summary>
    /// Function signature: the name, optionally with a parameter list —
    /// e.g. <c>"freshness"</c> or <c>"myScore(a,b)"</c>.
    /// </summary>
    public string Signature { get; }

    /// <summary>Ranking expression forming the function body.</summary>
    public string Expression { get; }

    /// <summary>
    /// Name of the rank profile the function belongs to. May be omitted when the
    /// document type declares exactly one rank profile; with multiple profiles,
    /// schema generation throws unless the profile is named explicitly.
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>
    /// Creates a rank function definition.
    /// </summary>
    /// <param name="signature">Function name, optionally with parameters (e.g. <c>"myScore(a,b)"</c>).</param>
    /// <param name="expression">Ranking expression forming the function body.</param>
    public VespaRankFunctionAttribute(string signature, string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        Signature = signature;
        Expression = expression;
    }
}
