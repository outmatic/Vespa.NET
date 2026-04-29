using System.Diagnostics;

namespace Vespa;

/// <summary>
/// A <see cref="DelegatingHandler"/> that records client-side metrics
/// for every HTTP request made by Vespa.NET.
/// Automatically wired when using <see cref="VespaServiceCollectionExtensions"/>.
/// </summary>
public sealed class VespaMetricsHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        VespaClientMetrics.RequestsTotal.Add(1);

        try
        {
            var response = await base.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                VespaClientMetrics.RequestErrors.Add(1);

            // Record semantic counters based on the request path
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.StartsWith("/search"))
                VespaClientMetrics.SearchRequests.Add(1);
            else if (path.StartsWith("/document/v1/") && request.Method == HttpMethod.Post)
                VespaClientMetrics.DocumentsWritten.Add(1);
            else if (path.StartsWith("/document/v1/") && request.Method == HttpMethod.Put)
                VespaClientMetrics.DocumentsWritten.Add(1);
            else if (path.StartsWith("/document/v1/") && request.Method == HttpMethod.Delete)
                VespaClientMetrics.DocumentsDeleted.Add(1);

            return response;
        }
        catch
        {
            VespaClientMetrics.RequestErrors.Add(1);
            throw;
        }
        finally
        {
            sw.Stop();
            VespaClientMetrics.RequestDuration.Record(sw.Elapsed.TotalMilliseconds);
        }
    }
}
