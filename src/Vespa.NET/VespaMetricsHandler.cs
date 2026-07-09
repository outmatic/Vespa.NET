using System.Diagnostics;

namespace Vespa;

/// <summary>
/// A <see cref="DelegatingHandler"/> that records client-side metrics
/// for every HTTP request made by Vespa.NET.
/// Automatically wired when using <see cref="VespaServiceCollectionExtensions"/>.
/// <para>Measurements carry <c>http.request.method</c>, <c>server.address</c>, and
/// <c>vespa.operation</c> tags, plus <c>http.response.status_code</c> (responses) or
/// <c>error.type</c> (transport failures). Caller-initiated cancellations are not
/// counted as request errors.</para>
/// </summary>
public sealed class VespaMetricsHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var path = request.RequestUri?.AbsolutePath ?? "";
        var tags = new TagList
        {
            { "http.request.method", request.Method.Method },
            { "server.address", request.RequestUri?.Host },
            { "vespa.operation", DeriveOperation(path, request.Method) }
        };

        try
        {
            var response = await base.SendAsync(request, cancellationToken);

            tags.Add("http.response.status_code", (int)response.StatusCode);
            VespaClientMetrics.RequestsTotal.Add(1, tags);
            if (!response.IsSuccessStatusCode)
                VespaClientMetrics.RequestErrors.Add(1, tags);

            // Record semantic counters based on the request path
            if (path.StartsWith("/search", StringComparison.Ordinal))
                VespaClientMetrics.SearchRequests.Add(1, tags);
            else if (path.StartsWith("/document/v1/", StringComparison.Ordinal) && (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put))
                VespaClientMetrics.DocumentsWritten.Add(1, tags);
            else if (path.StartsWith("/document/v1/", StringComparison.Ordinal) && request.Method == HttpMethod.Delete)
                VespaClientMetrics.DocumentsDeleted.Add(1, tags);

            return response;
        }
        // A caller-initiated cancellation is not a Vespa failure — recording it as a
        // request error would let an impatient caller look like a broken cluster.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            tags.Add("error.type", "cancelled");
            VespaClientMetrics.RequestsTotal.Add(1, tags);
            throw;
        }
        catch (Exception ex)
        {
            tags.Add("error.type", ex.GetType().Name);
            VespaClientMetrics.RequestsTotal.Add(1, tags);
            VespaClientMetrics.RequestErrors.Add(1, tags);
            throw;
        }
        finally
        {
            VespaClientMetrics.RequestDuration.Record(
                Stopwatch.GetElapsedTime(start).TotalMilliseconds, tags);
        }
    }

    private static string DeriveOperation(string path, HttpMethod method) => path switch
    {
        _ when path.StartsWith("/search", StringComparison.Ordinal) => "search",
        _ when path.StartsWith("/document/v1", StringComparison.Ordinal) => method.Method switch
        {
            "POST" => "document.put",
            "PUT" => "document.update",
            "DELETE" => "document.delete",
            _ => "document.get"
        },
        _ when path.StartsWith("/application/v2", StringComparison.Ordinal) => "admin",
        _ when path.StartsWith("/state/v1", StringComparison.Ordinal) => "state",
        _ => "other"
    };
}
