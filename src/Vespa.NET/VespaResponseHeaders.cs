namespace Vespa;

/// <summary>
/// Helpers for reading HTTP response headers into exception metadata.
/// </summary>
internal static class VespaResponseHeaders
{
    /// <summary>
    /// Reads the <c>Retry-After</c> header as a delay. Absolute dates are converted
    /// to a delay relative to now; past dates and negative deltas yield
    /// <see cref="TimeSpan.Zero"/>. Returns <see langword="null"/> when the header
    /// is absent or unparseable.
    /// </summary>
    internal static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
            return null;

        if (retryAfter.Delta is { } delta)
            return delta >= TimeSpan.Zero ? delta : TimeSpan.Zero;

        if (retryAfter.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }
}
