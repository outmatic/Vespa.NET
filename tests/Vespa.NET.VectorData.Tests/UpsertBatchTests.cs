using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.VectorData;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class UpsertBatchTests
{
    [Fact]
    public async Task EmbeddingRequestsAndRecordEnumeration_AreBoundedByTheirIndependentLimits()
    {
        using var generator = new BatchEmbeddingGenerator();
        var enumerated = 0;
        var writes = 0;
        using var harness = new TestHarness((_, _) =>
        {
            Interlocked.Increment(ref writes);
            Assert.True(enumerated - writes < 4);
            return TestHarness.Json("{}");
        });
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with
        {
            UpsertBatchSize = 4, EmbeddingBatchSize = 2, UpsertConcurrency = 1, EmbeddingGenerator = generator
        });
        IEnumerable<Dictionary<string, object?>> Records()
        {
            for (var i = 0; i < 9; i++)
            {
                enumerated++;
                yield return new() { ["Key"] = Guid.NewGuid(), ["UserId"] = "alice", ["Content"] = "text", ["Vector"] = "embed" };
            }
        }
        using var collection = store.GetDynamicCollection("memory", DynamicCollectionTests.Definition(typeof(string)));
        await collection.UpsertAsync(Records());
        Assert.Equal(9, writes);
        Assert.Equal(5, generator.Calls);
        Assert.Equal(new[] { 2, 2, 2, 2, 1 }, generator.BatchSizes);
    }

    [Fact]
    public async Task ParallelWrites_AreBoundedAndPreserveDuplicateKeyOrder()
    {
        var active = 0;
        var maximum = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var values = new ConcurrentDictionary<string, List<string>>();
        using var handler = new AsyncUpsertHandler(async (request, body, ct) =>
        {
            var current = Interlocked.Increment(ref active);
            lock (values)
                maximum = Math.Max(maximum, current);
            if (current == 3)
                gate.TrySetResult();
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await Task.Delay(10, ct);
            using var json = JsonDocument.Parse(body!);
            var key = request.RequestUri!.AbsolutePath.Split('/').Last();
            var titles = values.GetOrAdd(key, _ => []);
            lock (titles)
                titles.Add(json.RootElement.GetProperty("fields").GetProperty("title").GetString()!);
            Interlocked.Decrement(ref active);
            return TestHarness.Json("{}");
        });
        using var client = Client(handler);
        using var store = new VespaVectorStore(client, TestHarness.Options with { UpsertConcurrency = 3, UpsertBatchSize = 4 });
        await store.GetCollection<string, Article>("article").UpsertAsync(new[]
        {
            Record("a", "first"), Record("a", "second"), Record("b"), Record("c"), Record("a", "third")
        });
        Assert.Equal(3, maximum);
        Assert.Equal(new[] { "first", "second", "third" }, values["a"]);
    }

    [Fact]
    public async Task ReusedInputObjectsAndArrays_AreCapturedBeforeTheEnumeratorAdvances()
    {
        using var harness = new TestHarness();
        using var collection = harness.Store.GetCollection<string, BufferedRecord>("article");
        IEnumerable<BufferedRecord> Records()
        {
            var record = new BufferedRecord { Tags = [""], Embedding = new float[3] };
            for (var i = 0; i < 3; i++)
            {
                record.Id = "row-" + i;
                record.Tags[0] = "tag-" + i;
                Array.Clear(record.Embedding);
                record.Embedding[i] = 1;
                yield return record;
            }
        }
        await collection.UpsertAsync(Records());
        Assert.Equal(3, harness.Handler.Requests.Count);
        for (var i = 0; i < 3; i++)
        {
            var request = Assert.Single(harness.Handler.Requests, r => r.Uri.AbsolutePath.EndsWith("/row-" + i, StringComparison.Ordinal));
            using var body = JsonDocument.Parse(request.Body!);
            var fields = body.RootElement.GetProperty("fields");
            Assert.Equal("tag-" + i, fields.GetProperty("tags")[0].GetString());
            Assert.Equal(1, fields.GetProperty("embedding").GetProperty("values")[i].GetSingle());
        }
    }

    [Fact]
    public async Task FailedWrite_ReportsAcknowledgementsAndSkipsLaterWritesForThatKey()
    {
        using var harness = new TestHarness((request, _) => TestHarness.Json("{}",
            request.RequestUri!.AbsolutePath.EndsWith("/b", StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { UpsertBatchSize = 4, UpsertConcurrency = 1 });
        var enumerated = 0;
        IEnumerable<Article> Records()
        {
            foreach (var key in new[] { "a", "b", "b", "c", "never-enumerated" })
            {
                enumerated++;
                yield return Record(key);
            }
        }
        var error = await Assert.ThrowsAsync<VespaVectorUpsertException>(() => store.GetCollection<string, Article>("article").UpsertAsync(Records()));
        Assert.Equal(4, enumerated);
        Assert.False(error.InputFullyEnumerated);
        Assert.Equal(new[] { VespaVectorUpsertStatus.Succeeded, VespaVectorUpsertStatus.Unconfirmed,
            VespaVectorUpsertStatus.NotAttempted, VespaVectorUpsertStatus.Succeeded }, error.Outcomes.Select(o => o.Status));
        Assert.Equal(new long[] { 0, 1, 2, 3 }, error.Outcomes.Select(o => o.Index));
        Assert.NotNull(error.Outcomes[1].Error);
        Assert.Equal(3, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task InvalidLaterChunk_ReportsEarlierWritesAndDoesNotWriteTheInvalidChunk()
    {
        using var harness = new TestHarness();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { UpsertBatchSize = 2 });
        var error = await Assert.ThrowsAsync<VespaVectorUpsertException>(() => store.GetCollection<string, Article>("article").UpsertAsync(new[]
        {
            Record("a"), Record("b"), Record("c"), new Article { Id = "d", Embedding = new float[] { 0, 0, 0 } }
        }));
        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.Equal(2, error.Outcomes.Count(o => o.Status == VespaVectorUpsertStatus.Succeeded));
        Assert.All(error.Outcomes.Skip(2), o => Assert.Equal(VespaVectorUpsertStatus.NotAttempted, o.Status));
        Assert.IsType<ArgumentException>(error.InnerException);
    }

    [Fact]
    public async Task CallerCancellation_PreservesTokenAndEarlierAcknowledgements()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var handler = new AsyncUpsertHandler((_, _, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return Task.FromResult(TestHarness.Json("{}"));
        });
        using var client = Client(handler);
        using var store = new VespaVectorStore(client, TestHarness.Options with { UpsertConcurrency = 1 });
        var error = await Assert.ThrowsAsync<VespaVectorUpsertCanceledException>(() => store.GetCollection<string, Article>("article")
            .UpsertAsync(new[] { Record("a"), Record("b"), Record("c") }, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(error.InputFullyEnumerated);
        Assert.Equal(new[] { VespaVectorUpsertStatus.Succeeded, VespaVectorUpsertStatus.Unconfirmed,
            VespaVectorUpsertStatus.NotAttempted }, error.Outcomes.Select(o => o.Status));
    }

    [Theory]
    [InlineData("records")]
    [InlineData("embedding")]
    [InlineData("concurrency")]
    public void InvalidLimits_FailAtConstruction(string limit)
    {
        using var harness = new TestHarness();
        var options = limit switch
        {
            "records" => TestHarness.Options with { UpsertBatchSize = 0 },
            "embedding" => TestHarness.Options with { EmbeddingBatchSize = 0 },
            _ => TestHarness.Options with { UpsertConcurrency = 0 }
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => new VespaVectorStore(harness.Client, options));
    }

    [Fact]
    public async Task PreCanceledUpsert_HasNoOutcomesOrRequests()
    {
        using var harness = new TestHarness();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<VespaVectorUpsertCanceledException>(() => harness.Collection.UpsertAsync(Record("a"), cancellation.Token));
        Assert.Empty(error.Outcomes);
        Assert.False(error.InputFullyEnumerated);
        Assert.Empty(harness.Handler.Requests);
    }

    private static Article Record(string key, string? title = null) => new() { Id = key, Title = title ?? key, Embedding = new float[] { 1, 0, 0 } };
    private static VespaClient Client(HttpMessageHandler handler) => new(new HttpClient(handler), new() { Endpoint = "http://localhost:8080" });
}

public sealed class BufferedRecord
{
    [VectorStoreKey] public string Id { get; set; } = "";
    [VectorStoreData(StorageName = "tags")] public string[] Tags { get; set; } = [];
    [VectorStoreVector(3, StorageName = "embedding")] public float[] Embedding { get; set; } = [];
}

internal sealed class AsyncUpsertHandler(Func<HttpRequestMessage, string?, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        await respond(request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken), cancellationToken);
}
