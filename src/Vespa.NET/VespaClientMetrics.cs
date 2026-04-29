using System.Diagnostics.Metrics;

namespace Vespa;

/// <summary>
/// Client-side metrics for Vespa.NET using <see cref="System.Diagnostics.Metrics"/>.
/// Register the meter in your OpenTelemetry setup:
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithMetrics(m => m.AddMeter(VespaClientMetrics.MeterName));
/// </code>
/// </summary>
public static class VespaClientMetrics
{
    /// <summary>The meter name: <c>"Vespa.NET"</c></summary>
    public const string MeterName = "Vespa.NET";

    internal static readonly Meter Meter = new(MeterName);

    internal static readonly Counter<long> RequestsTotal =
        Meter.CreateCounter<long>("vespa.client.requests", "requests", "Total number of HTTP requests sent");

    internal static readonly Counter<long> RequestErrors =
        Meter.CreateCounter<long>("vespa.client.request_errors", "requests", "Number of failed HTTP requests");

    internal static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>("vespa.client.request_duration", "ms", "HTTP request duration in milliseconds");

    internal static readonly Counter<long> DocumentsWritten =
        Meter.CreateCounter<long>("vespa.client.documents_written", "documents", "Number of documents written (put/update)");

    internal static readonly Counter<long> DocumentsDeleted =
        Meter.CreateCounter<long>("vespa.client.documents_deleted", "documents", "Number of documents deleted");

    internal static readonly Counter<long> SearchRequests =
        Meter.CreateCounter<long>("vespa.client.search_requests", "requests", "Number of search requests");

    internal static readonly Counter<long> RetryAttempts =
        Meter.CreateCounter<long>("vespa.client.retry_attempts", "retries", "Number of retry attempts");
}
