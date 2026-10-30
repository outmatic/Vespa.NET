using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.VectorData;

namespace Vespa.VectorData.Tests;

internal sealed class TestHarness : IDisposable
{
    internal TestHarness(Func<HttpRequestMessage, string?, HttpResponseMessage>? respond = null)
    {
        Handler = new RecordingHandler(respond);
        Http = new HttpClient(Handler) { BaseAddress = new Uri("http://localhost:8080") };
        Client = new VespaClient(Http, new VespaClientOptions { Endpoint = "http://localhost:8080", DefaultNamespace = "test" });
        Store = new VespaVectorStore(Client, Options);
    }

    internal static VespaVectorStoreOptions Options => new() { Namespace = "test", RankProfile = "vector_search", Approximate = false };
    internal RecordingHandler Handler { get; }
    internal HttpClient Http { get; }
    internal VespaClient Client { get; }
    internal VespaVectorStore Store { get; }
    internal VectorStoreCollection<string, Article> Collection => Store.GetCollection<string, Article>("article");

    internal static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    internal static string Hit(string id = "id:test:article::a:1", double distance = 0, string category = "docs") => JsonSerializer.Serialize(new
    {
        root = new
        {
            children = new[]
            {
                new
                {
                    id = "index:content/0/synthetic",
                    relevance = 99.0,
                    fields = new Dictionary<string, object>
                    {
                        ["documentid"] = id,
                        ["title"] = "Vespa vector search",
                        ["category"] = category,
                        ["priority"] = 3,
                        ["published"] = true,
                        ["embedding"] = new { type = "tensor<float>(x[3])", values = new[] { 1f, 0, 0 } },
                        ["matchfeatures"] = new Dictionary<string, double> { ["distance(field,embedding)"] = distance }
                    }
                }
            }
        }
    });

    public void Dispose()
    {
        Store.Dispose();
        Client.Dispose();
        Http.Dispose();
    }
}

internal sealed class RecordingHandler(Func<HttpRequestMessage, string?, HttpResponseMessage>? respond) : HttpMessageHandler
{
    internal List<(HttpMethod Method, Uri Uri, string? Body)> Requests { get; } = [];
    internal bool Disposed { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Consume the actual body, including its asynchronous serialization and lifetime.
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!, body));
        return respond?.Invoke(request, body) ?? TestHarness.Json("{}");
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

public sealed class Article
{
    [VectorStoreKey]
    public string Id { get; set; } = "";
    [VectorStoreData(StorageName = "title")]
    public string Title { get; set; } = "";
    [VectorStoreData(StorageName = "category", IsIndexed = true)]
    public string Category { get; set; } = "";
    [VectorStoreData(StorageName = "priority", IsIndexed = true)]
    public int Priority { get; set; }
    [VectorStoreData(StorageName = "published", IsIndexed = true)]
    public bool Published { get; set; }
    [VectorStoreVector(3, StorageName = "embedding", DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}

internal static class AsyncResults
{
    internal static async Task<List<T>> Collect<T>(this IAsyncEnumerable<T> source)
    {
        List<T> result = [];
        await foreach (var item in source)
            result.Add(item);
        return result;
    }
}
