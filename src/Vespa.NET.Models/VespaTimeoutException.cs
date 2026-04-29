namespace Vespa.Models;

/// <summary>
/// Thrown when Vespa returns HTTP 408 (Request Timeout) or 504 (Gateway Timeout).
/// </summary>
public sealed class VespaTimeoutException(string message, VespaError? error = null, int statusCode = 504)
    : VespaException(message, error, statusCode);
