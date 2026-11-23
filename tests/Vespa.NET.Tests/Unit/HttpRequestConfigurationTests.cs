using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Vespa.Models;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class HttpRequestConfigurationTests
{
    [Theory]
    [InlineData("search")]
    [InlineData("selection")]
    [InlineData("jsonl")]
    public async Task RequestsHonorTheConfiguredHttpVersionAndPolicy(string operation)
    {
        using var handler = new RecordingHandler();
        using var services = BuildProvider(handler);
        var client = services.GetRequiredService<IVespaClient>();

        switch (operation)
        {
            case "search":
                handler.Enqueue("""{"root":{"children":[]}}""");
                await client.Search.QueryAsync<object>("select * from music where true");
                Assert.Single(handler.Requests);
                break;
            case "selection":
                handler.Enqueue("""{"documentCount":1,"continuation":"next"}""");
                handler.Enqueue("""{"documentCount":2}""");
                var result = await client.Documents.DeleteBySelectionAsync("music", "music");
                Assert.Equal(3, result.DocumentCount);
                Assert.Equal(2, handler.Requests.Count);
                break;
            case "jsonl":
                handler.Enqueue("""{"continuation":{"token":"next"}}""", mediaType: "application/jsonl");
                handler.Enqueue("""{"continuation":{"percentFinished":100}}""", mediaType: "application/jsonl");
                await using (var documents = client.Documents.VisitJsonlAsync<object>("music").GetAsyncEnumerator())
                    Assert.False(await documents.MoveNextAsync());
                Assert.Equal(2, handler.Requests.Count);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpVersion.Version30, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.Policy);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task SearchDisposesTheRequestBodyOnSuccessAndHttpError(HttpStatusCode status)
    {
        using var handler = new RecordingHandler();
        handler.Enqueue(status == HttpStatusCode.OK ? """{"root":{"children":[]}}""" : """{"message":"Invalid query"}""", status);
        using var services = BuildProvider(handler);
        var search = services.GetRequiredService<IVespaClient>().Search;

        if (status == HttpStatusCode.OK)
            await search.QueryAsync<object>("select * from music where true");
        else
            await Assert.ThrowsAnyAsync<VespaException>(() => search.QueryAsync<object>("select * from music where true"));

        var content = Assert.IsAssignableFrom<HttpContent>(handler.SentContent);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await content.ReadAsStringAsync());
    }

    private static ServiceProvider BuildProvider(RecordingHandler handler)
    {
        var services = new ServiceCollection();
        services.AddVespaClient(new VespaClientOptions { Endpoint = "https://vespa.example", EnableRetry = false }, http =>
        {
            http.DefaultRequestVersion = HttpVersion.Version30;
            http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<(Version Version, HttpVersionPolicy Policy)> Requests { get; } = [];
        public HttpContent? SentContent { get; private set; }

        public void Enqueue(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json") =>
            _responses.Enqueue(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Version, request.VersionPolicy));
            SentContent = request.Content;
            if (request.Content is { } content)
                Assert.NotEmpty(await content.ReadAsStringAsync(cancellationToken));
            return _responses.Dequeue();
        }
    }
}
