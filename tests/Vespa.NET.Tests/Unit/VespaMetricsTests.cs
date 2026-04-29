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

    public VespaMetricsTests()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == VespaClientMetrics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            _longCounters.AddOrUpdate(instrument.Name, measurement, (_, current) => current + measurement));
        _listener.SetMeasurementEventCallback<double>((_, measurement, _, _) =>
            _durations.Add(measurement));
        _listener.Start();
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
