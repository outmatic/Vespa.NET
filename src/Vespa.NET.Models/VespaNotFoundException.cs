namespace Vespa.Models;

/// <summary>
/// Thrown when Vespa returns HTTP 404 — the requested document or resource does not exist.
/// </summary>
public sealed class VespaNotFoundException(string message, VespaError? error = null)
    : VespaException(message, error, 404);
