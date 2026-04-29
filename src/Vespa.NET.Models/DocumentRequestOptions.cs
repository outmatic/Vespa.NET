namespace Vespa.Models;

/// <summary>
/// Optional parameters for Document API requests that map to Vespa query parameters.
/// Pass to document operations to control routing, tracing, and tensor format.
/// </summary>
public sealed record DocumentRequestOptions
{
    /// <summary>
    /// Message routing string (e.g. <c>"default/chain.indexing"</c>).
    /// Maps to the <c>route</c> query parameter.
    /// </summary>
    public string? Route { get; init; }

    /// <summary>
    /// Diagnostic trace level (0-9). Maps to the <c>tracelevel</c> query parameter.
    /// </summary>
    public int? TraceLevel { get; init; }

    /// <summary>
    /// Tensor format for response values. Maps to the <c>format.tensors</c> query parameter.
    /// Valid values: <c>short</c>, <c>long</c>, <c>short-value</c>, <c>long-value</c>.
    /// </summary>
    public string? TensorFormat { get; init; }

    /// <summary>
    /// When <c>true</c>, the request is a bandwidth test only — no documents are written.
    /// Maps to the <c>dryRun</c> query parameter.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Vespa server-side soft timeout for the request (serialized as <c>{ms}ms</c>).
    /// Distinct from the HTTP client timeout on <c>VespaClientOptions.Timeout</c>:
    /// this tells Vespa to abandon the request after the given duration and return a
    /// response, releasing server-side resources. Maps to the <c>timeout</c> query parameter.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Builds query parameter tuples for use in URL construction.
    /// </summary>
    public IEnumerable<(string Key, string? Value)> ToQueryParams()
    {
        if (Route is not null)
            yield return ("route", Route);
        if (TraceLevel.HasValue)
            yield return ("tracelevel", TraceLevel.Value.ToString());
        if (TensorFormat is not null)
            yield return ("format.tensors", TensorFormat);
        if (DryRun)
            yield return ("dryRun", "true");
        if (Timeout.HasValue)
            yield return ("timeout", $"{(long)Timeout.Value.TotalMilliseconds}ms");
    }
}
