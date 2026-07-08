namespace Vespa.Models;

/// <summary>Exception thrown when a Vespa operation fails.</summary>
public class VespaException : Exception
{
    /// <summary>Structured error details returned by Vespa, if available.</summary>
    public VespaError? Error { get; init; }

    /// <summary>HTTP status code associated with the failure.</summary>
    public int? StatusCode { get; init; }

    public VespaException(string message, VespaError? error = null, int? statusCode = null)
        : base(message)
    {
        Error = error;
        StatusCode = statusCode;
    }

    public VespaException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>
    /// Creates the most specific <see cref="VespaException"/> subtype for the given
    /// <paramref name="statusCode"/>. An empty <paramref name="message"/> is replaced
    /// with a status-specific default. <paramref name="retryAfter"/> (from the
    /// <c>Retry-After</c> header) is carried by
    /// <see cref="VespaTooManyRequestsException.RetryAfter"/> for HTTP 429.
    /// </summary>
    public static VespaException FromStatusCode(int statusCode, string message, VespaError? error = null, TimeSpan? retryAfter = null)
    {
        var effectiveMessage = string.IsNullOrEmpty(message) ? DefaultMessage(statusCode) : message;

        return statusCode switch
        {
            404 => new VespaNotFoundException(effectiveMessage, error),
            412 => new VespaConditionNotMetException(effectiveMessage, error),
            408 or 504 => new VespaTimeoutException(effectiveMessage, error, statusCode),
            429 => new VespaTooManyRequestsException(effectiveMessage, error, retryAfter),
            >= 500 => new VespaServerException(effectiveMessage, error, statusCode),
            _ => new VespaException(effectiveMessage, error, statusCode)
        };
    }

    private static string DefaultMessage(int statusCode) => statusCode switch
    {
        404 => "Not found (HTTP 404): the requested document or resource does not exist.",
        412 => "Condition not met (HTTP 412): the document did not satisfy the condition.",
        408 or 504 => $"Request timed out (HTTP {statusCode}).",
        429 => "Too many requests (HTTP 429): back off and retry.",
        >= 500 => $"Vespa server error (HTTP {statusCode}).",
        _ => $"Vespa request failed (HTTP {statusCode})."
    };
}
