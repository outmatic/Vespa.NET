namespace Vespa.Models;

/// <summary>
/// Thrown when Vespa returns HTTP 412 — a conditional write was rejected because
/// the document did not satisfy the specified condition.
/// </summary>
public sealed class VespaConditionNotMetException(string message, VespaError? error = null)
    : VespaException(message, error, 412);
