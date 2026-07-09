using System.Text.Json;
using Vespa.Models;

namespace Vespa;

/// <summary>
/// Builds the most specific <see cref="VespaException"/> from an HTTP error response,
/// parsing the structured <see cref="VespaError"/> body when present. Shared by the
/// document, search, and admin error paths so all three surface the same fidelity.
/// </summary>
internal static class VespaErrorResponse
{
    /// <summary>
    /// Creates the exception for a non-success <paramref name="response"/>.
    /// The body is read to a string exactly once — it may not be JSON (e.g. an HTML
    /// 502 page from a proxy), and unbuffered <c>ResponseHeadersRead</c> content
    /// cannot be read twice. <paramref name="context"/>, when set, prefixes the
    /// resulting message (e.g. <c>"Search failed"</c>).
    /// </summary>
    internal static async Task<VespaException> CreateAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        string? context = null)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        VespaError? error = null;
        if (!string.IsNullOrWhiteSpace(body))
            try
            {
                error = JsonSerializer.Deserialize<VespaError>(body, VespaJsonOptions.Default);
            }
            catch (JsonException)
            {
                // Non-JSON error body — fall through to the raw-body message.
            }

        var message = error?.Message ?? $"HTTP {(int)response.StatusCode}: {body}";
        if (context is not null)
            message = $"{context}: {message}";

        return VespaException.FromStatusCode(
            (int)response.StatusCode, message, error, VespaResponseHeaders.GetRetryAfter(response));
    }
}
