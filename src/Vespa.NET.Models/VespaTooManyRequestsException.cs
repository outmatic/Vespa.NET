namespace Vespa.Models;

/// <summary>
/// Exception thrown when Vespa responds with HTTP 429 (Too Many Requests) —
/// the standard backpressure signal under feed or query overload.
/// Callers should back off and retry.
/// </summary>
public sealed class VespaTooManyRequestsException : VespaException
{
    public VespaTooManyRequestsException(string message, VespaError? error = null)
        : base(message, error, 429)
    {
    }
}
