namespace Vespa.Models;

/// <summary>Exception thrown when a Vespa operation fails.</summary>
public class VespaException : Exception
{
    /// <summary>Structured error details returned by Vespa, if available.</summary>
    public VespaError? Error { get; init; }

    /// <summary>HTTP status code associated with the failure.</summary>
    public int? StatusCode { get; init; }

    public VespaException(string message) : base(message) { }

    public VespaException(string message, Exception innerException)
        : base(message, innerException) { }

    public VespaException(string message, VespaError? error, int? statusCode = null)
        : base(message)
    {
        Error = error;
        StatusCode = statusCode;
    }

    public VespaException(string message, int statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// Creates the most specific <see cref="VespaException"/> subtype for the given
    /// <paramref name="statusCode"/>.
    /// </summary>
    public static VespaException FromStatusCode(int statusCode, string message, VespaError? error = null)
        => statusCode switch
        {
            404 => new VespaNotFoundException(message, error),
            412 => new VespaConditionNotMetException(
                string.IsNullOrEmpty(message)
                    ? "Condition not met (HTTP 412): the document did not satisfy the condition."
                    : message,
                error),
            408 or 504 => new VespaTimeoutException(message, error, statusCode),
            429 => new VespaTooManyRequestsException(message, error),
            >= 500 => new VespaServerException(message, error, statusCode),
            _ => new VespaException(message, error, statusCode)
        };
}
