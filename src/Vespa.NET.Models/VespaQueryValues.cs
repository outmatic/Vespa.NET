namespace Vespa.Models;

/// <summary>
/// Formatting helpers for Vespa query-parameter values, shared by the request-option records.
/// </summary>
internal static class VespaQueryValues
{
    /// <summary>
    /// Serializes a <see cref="TimeSpan"/> as integral milliseconds with the
    /// <c>ms</c> suffix Vespa expects (e.g. <c>2500ms</c>).
    /// </summary>
    internal static string ToMilliseconds(TimeSpan value) => $"{(long)value.TotalMilliseconds}ms";
}
