namespace Vespa.Models;

/// <summary>
/// Thrown when Vespa returns an HTTP 5xx server error.
/// </summary>
public sealed class VespaServerException(string message, VespaError? error = null, int statusCode = 500)
    : VespaException(message, error, statusCode);
