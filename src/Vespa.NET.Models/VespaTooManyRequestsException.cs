namespace Vespa.Models;

/// <summary>
/// Exception thrown when Vespa responds with HTTP 429 (Too Many Requests) —
/// the standard backpressure signal under feed or query overload.
/// Callers should back off (see <see cref="RetryAfter"/>) and retry.
/// </summary>
public sealed class VespaTooManyRequestsException(string message, VespaError? error = null, TimeSpan? retryAfter = null)
    : VespaException(message, error, 429)
{
    /// <summary>
    /// Server-suggested backoff parsed from the <c>Retry-After</c> response header,
    /// or <see langword="null"/> when the header was absent. Absolute dates are
    /// converted to a delay relative to the time the response was received.
    /// </summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
