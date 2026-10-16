using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Vespa.Documents;
using Vespa.Feed;
using Vespa.Models;
using Vespa.Models.Attributes;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed partial class MixedFeedTests
{
    [Fact]
    public async Task MixedFeed_ForwardsBodiesConditionsCreateFlagsAndRequestOptions()
    {
        using var harness = new Harness();
        var options = new DocumentRequestOptions { Timeout = TimeSpan.FromSeconds(2), Route = "default/chain.indexing", TraceLevel = 3, DryRun = true };
        FeedOperation[] operations =
        [
            FeedOperation.Put("item", new Item { Price = 10 }, condition: "inventory.unit_price > 0", create: true, requestOptions: options),
            FeedOperation.Update<Item>("item", update => update.Field(item => item.Price, FieldOp.Assign(20)),
                createIfMissing: true, condition: "inventory.unit_price == 10", requestOptions: options),
            FeedOperation.Delete<Item>("item", condition: "inventory.unit_price == 20", requestOptions: options)
        ];

        var result = await harness.Feed.FeedAsync(operations);

        Assert.Equal(3, result.TotalOperations);
        Assert.Equal(result.TotalDocuments, result.TotalOperations);
        Assert.Equal(3, result.SuccessCount);
        Assert.Empty(result.Errors);
        var requests = harness.Requests.ToArray();
        Assert.Equal(["POST", "PUT", "DELETE"], requests.Select(request => request.Method));
        Assert.All(requests, request =>
        {
            Assert.Equal("/document/v1/tenant/inventory/docid/item", request.Uri.AbsolutePath);
            var query = Query(request.Uri);
            Assert.Equal("2000ms", query["timeout"]);
            Assert.Equal("default/chain.indexing", query["route"]);
            Assert.Equal("3", query["tracelevel"]);
            Assert.Equal("true", query["dryRun"]);
        });
        Assert.Equal("true", Query(requests[0].Uri)["create"]);
        Assert.Equal("true", Query(requests[1].Uri)["create"]);
        Assert.Equal("inventory.unit_price == 20", Query(requests[2].Uri)["condition"]);
        using var put = JsonDocument.Parse(requests[0].Body!);
        using var update = JsonDocument.Parse(requests[1].Body!);
        Assert.Equal(10, put.RootElement.GetProperty("fields").GetProperty("unit_price").GetInt32());
        Assert.Equal(20, update.RootElement.GetProperty("fields").GetProperty("unit_price").GetProperty("assign").GetInt32());
        Assert.Null(requests[2].Body);
    }

    [Fact]
    public async Task MixedFeed_SnapshotsUpdateFieldsAndBuilder()
    {
        using var harness = new Harness();
        Dictionary<string, FieldOperation> fields = new() { ["unit_price"] = FieldOp.Assign(10) };
        var dictionaryOperation = FeedOperation.Update<Item>("first", fields);
        TypedFieldUpdateBuilder<Item>? captured = null;
        var builderOperation = FeedOperation.Update<Item>("second", builder =>
        {
            captured = builder;
            builder.Field(item => item.Price, FieldOp.Assign(20));
        });
        fields["unit_price"] = FieldOp.Assign(100);
        captured!.Field(item => item.Price, FieldOp.Assign(200));

        var result = await harness.Feed.FeedAsync([dictionaryOperation, builderOperation]);

        Assert.Equal(2, result.SuccessCount);
        var values = harness.Requests.Select(request =>
        {
            using var body = JsonDocument.Parse(request.Body!);
            return body.RootElement.GetProperty("fields").GetProperty("unit_price").GetProperty("assign").GetInt32();
        }).Order().ToArray();
        Assert.Equal([10, 20], values);
    }

    [Fact]
    public async Task MixedFeed_FailuresAreCountedPerOperationWithSourceIdentity()
    {
        using var harness = new Harness((request, _) => Task.FromResult(request.Method == "PUT"
            ? Response(HttpStatusCode.PreconditionFailed, "condition failed")
            : Response(ignoredFields: request.Method == "POST")), new()
            {
                DefaultNamespace = "fallback",
                EnableRetry = false,
                FeedMaxRetainedErrors = 1
            });
        FeedOperation[] operations =
        [
            Put("item"),
            FeedOperation.Update<Item>("item", update => update.Field(item => item.Price, FieldOp.Assign(20))),
            FeedOperation.Delete<Item>("item"),
            FeedOperation.Update<Item>("item", update => update.Field(item => item.Price, FieldOp.Assign(30)))
        ];

        var result = await harness.Feed.FeedAsync(operations);

        Assert.Equal(4, result.TotalOperations);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(2, result.FailureCount);
        Assert.True(result.ErrorsTruncated);
        Assert.True(result.HadIgnoredFields);
        Assert.False(result.IsSuccess);
        var error = Assert.Single(result.Errors);
        Assert.Equal("item", error.DocumentId);
        Assert.Equal("inventory", error.DocumentType);
        Assert.Equal("tenant", error.Namespace);
        Assert.Equal(FeedOperationKind.Update, error.OperationKind);
        Assert.Equal(1, error.OperationIndex);
        Assert.Equal(412, error.StatusCode);
        Assert.Contains("condition failed", error.Message);
        Assert.Equal(["POST", "PUT", "DELETE", "PUT"], harness.Requests.Select(request => request.Method));
    }

    [Fact]
    public async Task MixedFeed_ProgressIdentifiesEachOperation()
    {
        using var harness = new Harness();
        var progress = new ConcurrentQueue<FeedProgress>();
        FeedOperation[] operations = [Put("item"), FeedOperation.Delete<Item>("item"), FeedOperation.Delete("other", "other_schema")];

        await harness.Feed.FeedAsync(operations, new() { OnProgress = progress.Enqueue });

        Assert.Equal([1, 2, 3], progress.Select(value => value.ProcessedCount).Order());
        Assert.Equal([0, 1, 2], progress.Select(value => value.OperationIndex!.Value).Order());
        Assert.All(progress, value =>
        {
            var operation = operations[value.OperationIndex!.Value];
            Assert.Equal(operation.Id, value.LastDocumentId);
            Assert.Equal(operation.Kind, value.LastOperationKind);
            Assert.Equal(operation.DocumentType, value.LastDocumentType);
            Assert.Equal(operation.Namespace ?? "tenant", value.LastNamespace);
            Assert.True(value.SuccessCount + value.FailureCount >= value.ProcessedCount);
        });
    }

    [Theory]
    [InlineData(400)]
    [InlineData(412)]
    [InlineData(500)]
    public async Task MixedFeed_NonRetryableErrorDoesNotBlockNextOperation(int status)
    {
        using var harness = new Harness((request, _) => Task.FromResult(request.Method == "POST" ? Response((HttpStatusCode)status) : Response()),
            new() { DefaultNamespace = "tenant", EnableRetry = true, FeedRetryBaseDelay = TimeSpan.FromMilliseconds(1) });

        var result = await harness.Feed.FeedAsync([Put("item"), FeedOperation.Delete<Item>("item")]);

        Assert.Equal(1, result.FailureCount);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(status, Assert.Single(result.Errors).StatusCode);
        Assert.Equal(["POST", "DELETE"], harness.Requests.Select(request => request.Method));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public async Task MixedFeed_ValidatesOptionsBeforeEnumeratingSource(int concurrency, int capacity)
    {
        using var harness = new Harness();
        var enumerated = false;
        IEnumerable<FeedOperation> Source()
        {
            enumerated = true;
            yield return Put("item");
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Feed.FeedAsync(Source(), new() { MaxConcurrency = concurrency, BoundedCapacity = capacity }));
        Assert.False(enumerated);
        Assert.Empty(harness.Requests);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task MixedFeed_ExhaustedRetryBudgetRecordsOneFailure(int status)
    {
        using var harness = new Harness((_, _) => Task.FromResult(Response((HttpStatusCode)status)),
            new() { DefaultNamespace = "tenant", EnableRetry = true, MaxRetryAttempts = 2, FeedRetryBaseDelay = TimeSpan.FromMilliseconds(1) });

        var result = await harness.Feed.FeedAsync([Put("item")]);

        Assert.Equal(3, harness.Requests.Count);
        Assert.Equal(1, result.TotalOperations);
        Assert.Equal(1, result.FailureCount);
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(status, Assert.Single(result.Errors).StatusCode);
    }

    [Fact]
    public async Task MixedFeed_HttpTimeoutIsAnIndividualFailure()
    {
        using var harness = new Harness((request, _) => request.Method == "POST"
            ? throw new TaskCanceledException("HTTP timeout")
            : Task.FromResult(Response()));

        var result = await harness.Feed.FeedAsync([Put("item"), FeedOperation.Delete<Item>("item")]);

        Assert.Equal(1, result.FailureCount);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, Assert.Single(result.Errors).OperationIndex);
    }

    [Fact]
    public async Task MixedFeed_EmptyAndNullSources()
    {
        using var harness = new Harness();
        var result = await harness.Feed.FeedAsync(Array.Empty<FeedOperation>());
        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.TotalOperations);
        Assert.Empty(harness.Requests);
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Feed.FeedAsync((IEnumerable<FeedOperation>)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Feed.FeedAsync((IAsyncEnumerable<FeedOperation>)null!));
    }

    private static FeedOperation Put(string id) => FeedOperation.Put(id, new Item());

    private static HttpResponseMessage Response(HttpStatusCode status = HttpStatusCode.OK, string message = "failed", bool ignoredFields = false)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(new { message })) };
        if (ignoredFields)
            response.Headers.Add("X-Vespa-Ignored-Fields", "true");
        return response;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

    private sealed record Request(string Method, Uri Uri, string? Body);

    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _http;
        internal ConcurrentQueue<Request> Requests { get; } = new();
        internal FeedOperations Feed { get; }

        internal Harness(Func<Request, CancellationToken, Task<HttpResponseMessage>>? respond = null, VespaClientOptions? options = null)
        {
            options ??= new() { DefaultNamespace = "tenant", EnableRetry = false };
            _http = new(new CallbackHandler(async (request, token) =>
            {
                Requests.Enqueue(request);
                return respond is null ? Response() : await respond(request, token);
            }))
            { BaseAddress = new Uri("http://localhost:8080") };
            Feed = new(new DocumentOperations(_http, options), options);
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class CallbackHandler(Func<Request, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(new(request.Method.Method, request.RequestUri!, body), cancellationToken);
        }
    }

    [VespaDocument("inventory", Namespace = "tenant")]
    private sealed class Item
    {
        [VespaField(Name = "unit_price")]
        public int Price { get; init; }
    }
}
