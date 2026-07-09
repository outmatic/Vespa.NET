using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Moq;
using Moq.Protected;
using Vespa;
using Xunit;

namespace Vespa.Tests.Unit;

public class VespaMetricsTests : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ConcurrentDictionary<string, long> _longCounters = new();
    private readonly ConcurrentBag<double> _durations = [];

    private readonly ConcurrentDictionary<string, Dictionary<string, object?>> _lastTags = new();

    public VespaMetricsTests()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == VespaClientMetrics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            _longCounters.AddOrUpdate(instrument.Name, measurement, (_, current) => current + measurement);
            _lastTags[instrument.Name] = ToDictionary(tags);
        });
        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            _durations.Add(measurement);
            _lastTags[instrument.Name] = ToDictionary(tags);
        });
        _listener.Start();
    }

    private static Dictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var tag in tags)
            dict[tag.Key] = tag.Value;
        return dict;
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public void Meter_HasCorrectName()
    {
        Assert.Equal("Vespa.NET", VespaClientMetrics.MeterName);
    }

    [Fact]
    public async Task MetricsHandler_RecordsRequestsTotal()
    {
        var handler = CreateHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };

        var before = GetCounter("vespa.client.requests");
        await client.GetAsync("/state/v1/health");

        Assert.True(GetCounter("vespa.client.requests") > before);
    }

    [Fact]
    public async Task MetricsHandler_RecordsErrors()
    {
        var handler = CreateHandler(HttpStatusCode.InternalServerError);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };

        var before = GetCounter("vespa.client.request_errors");
        await client.GetAsync("/state/v1/health");

        Assert.True(GetCounter("vespa.client.request_errors") > before);
    }

    [Fact]
    public async Task MetricsHandler_RecordsDuration()
    {
        var handler = CreateHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };

        var countBefore = _durations.Count;
        await client.GetAsync("/state/v1/health");

        Assert.True(_durations.Count > countBefore);
        Assert.True(_durations.Last() >= 0);
    }

    [Fact]
    public async Task MetricsHandler_RecordsSearchRequests()
    {
        var handler = CreateHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };

        var before = GetCounter("vespa.client.search_requests");
        await client.GetAsync("/search/?yql=select...");

        Assert.True(GetCounter("vespa.client.search_requests") > before);
    }

    [Fact]
    public async Task MetricsHandler_RecordsDocumentsPut()
    {
        var handler = CreateHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };

        var before = GetCounter("vespa.client.documents_written");
        await client.PostAsync("/document/v1/ns/type/docid/1", null);

        Assert.True(GetCounter("vespa.client.documents_written") > before);
    }

    [Fact]
    public async Task MetricsHandler_RecordsDocumentsDeleted()
    {
        var handler = CreateHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };

        var before = GetCounter("vespa.client.documents_deleted");
        await client.DeleteAsync("/document/v1/ns/type/docid/1");

        Assert.True(GetCounter("vespa.client.documents_deleted") > before);
    }

    [Fact]
    public async Task MetricsHandler_OnException_RecordsError()
    {
        var inner = new Mock<HttpMessageHandler>();
        inner.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("boom"));

        var handler = new VespaMetricsHandler { InnerHandler = inner.Object };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };

        var before = GetCounter("vespa.client.request_errors");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/test"));

        Assert.True(GetCounter("vespa.client.request_errors") > before);
    }

    // --- Connection pool tuning options ---

    [Fact]
    public void ConnectionPoolOptions_DefaultValues()
    {
        var opts = new VespaClientOptions { Endpoint = "http://localhost:8080" };
        Assert.Null(opts.InitialHttp2StreamWindowSize);
        Assert.Null(opts.ConnectTimeout);
    }

    [Fact]
    public void ConnectionPoolOptions_CanBeSet()
    {
        var opts = new VespaClientOptions
        {
            Endpoint = "http://localhost:8080",
            InitialHttp2StreamWindowSize = 1024 * 1024,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        Assert.Equal(1024 * 1024, opts.InitialHttp2StreamWindowSize);
        Assert.Equal(TimeSpan.FromSeconds(5), opts.ConnectTimeout);
    }

    [Fact]
    public async Task MetricsHandler_TagsMeasurements_WithMethodHostOperationAndStatus()
    {
        var handler = CreateHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };

        await client.PostAsync("/search/", new StringContent("{}"));

        var tags = _lastTags["vespa.client.requests"];
        Assert.Equal("POST", tags["http.request.method"]);
        Assert.Equal("localhost", tags["server.address"]);
        Assert.Equal("search", tags["vespa.operation"]);
        Assert.Equal(200, tags["http.response.status_code"]);
    }

    [Fact]
    public async Task MetricsHandler_TagsErrors_WithStatusCode()
    {
        var handler = CreateHandler(HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };

        await client.DeleteAsync("/document/v1/ns/type/docid/1");

        var tags = _lastTags["vespa.client.request_errors"];
        Assert.Equal(503, tags["http.response.status_code"]);
        Assert.Equal("document.delete", tags["vespa.operation"]);
    }

    [Fact]
    public async Task MetricsHandler_OnTransportException_TagsErrorType()
    {
        var inner = new Mock<HttpMessageHandler>();
        inner.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("boom"));

        var handler = new VespaMetricsHandler { InnerHandler = inner.Object };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/test"));

        var tags = _lastTags["vespa.client.request_errors"];
        Assert.Equal(nameof(HttpRequestException), tags["error.type"]);
    }

    [Fact]
    public async Task MetricsHandler_CallerCancellation_IsNotCountedAsRequestError()
    {
        using var cts = new CancellationTokenSource();
        var inner = new Mock<HttpMessageHandler>();
        inner.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage _, CancellationToken ct) =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var handler = new VespaMetricsHandler { InnerHandler = inner.Object };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:8080/search/");

        var errorsBefore = GetCounter("vespa.client.request_errors");
        var totalBefore = GetCounter("vespa.client.requests");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            invoker.SendAsync(request, cts.Token));

        // Counted as a request (tagged error.type=cancelled) but never as a request error
        Assert.Equal(errorsBefore, GetCounter("vespa.client.request_errors"));
        Assert.True(GetCounter("vespa.client.requests") > totalBefore);
        Assert.Equal("cancelled", _lastTags["vespa.client.requests"]["error.type"]);
    }

    private static VespaMetricsHandler CreateHandler(HttpStatusCode statusCode)
    {
        var inner = new Mock<HttpMessageHandler>();
        inner.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(statusCode));
        return new VespaMetricsHandler { InnerHandler = inner.Object };
    }

    private long GetCounter(string name) =>
        _longCounters.GetValueOrDefault(name);
}
