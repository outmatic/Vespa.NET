using System.Net;
using Vespa.Testcontainers;
using Xunit;

namespace Vespa.IntegrationTests;

public sealed class VespaContainerReadinessTests
{
    private const string Endpoint = "http://vespa.example";

    [Fact]
    public async Task DeadlineCancelsAnInFlightHealthRequest()
    {
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var canceled = false;
        using var http = new HttpClient(new Probe(async token =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                throw;
            }
            throw new InvalidOperationException("The probe should remain blocked until canceled.");
        }));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VespaContainer.WaitForApplicationReadyAsync(http, Endpoint, TimeSpan.FromMilliseconds(150), safety.Token));

        Assert.Contains("did not become ready", exception.Message);
        Assert.True(canceled);
        Assert.False(safety.IsCancellationRequested);
    }

    [Fact]
    public async Task SuccessAfterTheDeadlineDoesNotReportReadiness()
    {
        var clock = new TestClock();
        using var http = new HttpClient(new Probe(token =>
        {
            Assert.False(token.IsCancellationRequested);
            clock.Timestamp += TimeSpan.FromMinutes(2).Ticks;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            VespaContainer.WaitForApplicationReadyAsync(http, Endpoint, TimeSpan.FromMinutes(1), default, clock));
    }

    [Fact]
    public async Task CallerCancellationRetainsTheCallerToken()
    {
        using var caller = new CancellationTokenSource();
        using var http = new HttpClient(new Probe(token =>
        {
            caller.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            VespaContainer.WaitForApplicationReadyAsync(http, Endpoint, TimeSpan.FromSeconds(30), caller.Token));
        Assert.Equal(caller.Token, canceled.CancellationToken);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(4294967295L)]
    public async Task InvalidReadinessTimeoutIsRejectedBeforeDeployment(long milliseconds)
    {
        await using var vespa = new VespaContainer();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>("readyTimeout", () =>
            vespa.DeployApplicationAsync("unused.zip", readyTimeout: TimeSpan.FromMilliseconds(milliseconds)));
    }

    private sealed class Probe(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(Endpoint + "/state/v1/health", request.RequestUri?.AbsoluteUri);
            return respond(cancellationToken);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }
}
