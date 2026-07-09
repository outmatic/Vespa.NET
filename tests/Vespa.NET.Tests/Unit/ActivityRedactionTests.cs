using System.Diagnostics;
using System.Net;
using Moq;
using Moq.Protected;
using Vespa;
using Vespa.Documents;
using Vespa.Models;
using Vespa.Search;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for <see cref="VespaClientOptions.RecordQueryTextInTraces"/>: YQL text and
/// document ids must be excluded from OpenTelemetry activity tags unless opted in.
/// </summary>
public class ActivityRedactionTests : IDisposable
{
    private readonly Mock<HttpMessageHandler> _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly List<Activity> _activities = [];
    private readonly ActivityListener _listener;

    public ActivityRedactionTests()
    {
        _mockHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHandler.Object)
        {
            BaseAddress = new Uri("http://localhost:8080")
        };

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == VespaActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_activities)
                    _activities.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _httpClient.Dispose();
    }

    private void SetupOkResponse(string json = "{}") =>
        _mockHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });

    private static VespaClientOptions BuildOptions(bool recordQueryText) => new()
    {
        Endpoint = "http://localhost:8080",
        DefaultNamespace = "test",
        RecordQueryTextInTraces = recordQueryText
    };

    /// <summary>
    /// The listener is global while xUnit runs other test classes in parallel —
    /// scope the operation under a root activity and match by its trace id.
    /// </summary>
    private async Task<Activity> CaptureActivityAsync(string name, Func<Task> operation)
    {
        using var root = new Activity($"test-root-{Guid.NewGuid():N}");
        root.SetIdFormat(ActivityIdFormat.W3C);
        root.Start();
        var traceId = root.TraceId;

        await operation();

        root.Stop();

        lock (_activities)
        {
            var match = _activities.FirstOrDefault(a => a.OperationName == name && a.TraceId == traceId);
            Assert.NotNull(match);
            return match!;
        }
    }

    [Fact]
    public async Task SearchAsync_ByDefault_DoesNotTagYql()
    {
        SetupOkResponse("""{"root":{"fields":{"totalCount":0},"children":[]}}""");
        var searchOps = new SearchOperations(_httpClient, BuildOptions(recordQueryText: false));

        var activity = await CaptureActivityAsync("vespa.search", () =>
            searchOps.SearchAsync<TestDoc>(new VespaSearchRequest
            {
                Yql = "select * from testdoc where title contains 'secret user input'"
            }));

        Assert.DoesNotContain(activity.TagObjects, t => t.Key == "vespa.yql");
    }

    [Fact]
    public async Task SearchAsync_WhenOptedIn_TagsYql()
    {
        SetupOkResponse("""{"root":{"fields":{"totalCount":0},"children":[]}}""");
        var searchOps = new SearchOperations(_httpClient, BuildOptions(recordQueryText: true));

        const string yql = "select * from testdoc where title contains 'query text'";
        var activity = await CaptureActivityAsync("vespa.search", () =>
            searchOps.SearchAsync<TestDoc>(new VespaSearchRequest { Yql = yql }));

        var tag = Assert.Single(activity.TagObjects, t => t.Key == "vespa.yql");
        Assert.Equal(yql, tag.Value);
    }

    [Fact]
    public async Task DocumentGet_ByDefault_DoesNotTagDocumentId_ButKeepsTypeAndNamespace()
    {
        SetupOkResponse("""{"id":"id:test:testdoc::user-42","fields":{}}""");
        var docOps = new DocumentOperations(_httpClient, BuildOptions(recordQueryText: false));

        var activity = await CaptureActivityAsync("vespa.document.get", () =>
            docOps.GetAsync<TestDoc>("user-42", "testdoc"));

        Assert.DoesNotContain(activity.TagObjects, t => t.Key == "vespa.document_id");
        Assert.Contains(activity.TagObjects, t => t.Key == "vespa.document_type");
        Assert.Contains(activity.TagObjects, t => t.Key == "vespa.namespace");
    }

    [Fact]
    public async Task DocumentGet_WhenOptedIn_TagsDocumentId()
    {
        SetupOkResponse("""{"id":"id:test:testdoc::user-42","fields":{}}""");
        var docOps = new DocumentOperations(_httpClient, BuildOptions(recordQueryText: true));

        var activity = await CaptureActivityAsync("vespa.document.get", () =>
            docOps.GetAsync<TestDoc>("user-42", "testdoc"));

        var tag = Assert.Single(activity.TagObjects, t => t.Key == "vespa.document_id");
        Assert.Equal("user-42", tag.Value);
    }

    private sealed class TestDoc
    {
        public string? Title { get; set; }
    }
}
