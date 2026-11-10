using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class DynamicCollectionTests
{
    [Fact]
    public async Task GuidRecord_UsesCanonicalDocumentIdentityAndMappedFields()
    {
        using var harness = new TestHarness();
        using var collection = harness.Store.GetDynamicCollection("memory", Definition());
        var key = Guid.NewGuid();
        await collection.UpsertAsync(Record(key, new float[] { 1, 0, 0 }));
        var request = Assert.Single(harness.Handler.Requests);
        Assert.EndsWith($"/memory/docid/{key:D}", request.Uri.AbsoluteUri);
        using var json = JsonDocument.Parse(request.Body!);
        var fields = json.RootElement.GetProperty("fields");
        Assert.False(fields.TryGetProperty("Key", out _));
        Assert.Equal("alice", fields.GetProperty("user_id").GetString());
        Assert.Equal(3, fields.GetProperty("embedding").GetProperty("values").GetArrayLength());
        await Assert.ThrowsAsync<ArgumentException>(() => collection.GetAsync(key.ToString("D")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DynamicRead_RestoresGuidAndProjectsVectors(bool includeVectors)
    {
        var key = Guid.NewGuid();
        using var harness = new TestHarness((_, _) => TestHarness.Json(JsonSerializer.Serialize(new
        {
            fields = new
            {
                user_id = "alice",
                content = "Saved preference",
                embedding = new { values = new float[] { 1, 0, 0 } }
            }
        })));
        using var collection = harness.Store.GetDynamicCollection("memory", Definition());
        var result = await collection.GetAsync(key, new() { IncludeVectors = includeVectors });
        Assert.NotNull(result);
        Assert.Equal(key, Assert.IsType<Guid>(result["Key"]));
        Assert.Equal("Saved preference", result["Content"]);
        Assert.Equal(includeVectors, result.ContainsKey("Vector"));
        if (includeVectors)
            Assert.Equal(new float[] { 1, 0, 0 }, Assert.IsType<ReadOnlyMemory<float>>(result["Vector"]).ToArray());
    }

    [Fact]
    public async Task BatchStringVectors_EmbedOnceWithDocumentPrefix_BeforeWriting()
    {
        using var harness = new TestHarness();
        using var generator = new BatchEmbeddingGenerator();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with
        {
            EmbeddingGenerator = generator,
            DocumentEmbeddingPrefix = "search_document: "
        });
        using var collection = store.GetDynamicCollection("memory", Definition(typeof(string)));
        var records = new[] { Record(Guid.NewGuid(), "first"), Record(Guid.NewGuid(), "second") };
        await collection.UpsertAsync(records);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(new[] { "search_document: first", "search_document: second" }, generator.Inputs);
        Assert.Equal("first", records[0]["Vector"]);
        Assert.Equal(2, harness.Handler.Requests.Count);
        foreach (var request in harness.Handler.Requests)
        {
            using var body = JsonDocument.Parse(request.Body!);
            Assert.Equal(3, body.RootElement.GetProperty("fields").GetProperty("embedding").GetProperty("values").GetArrayLength());
        }
    }

    [Fact]
    public async Task PrecomputedVectors_BypassEmbeddingGenerator_InAStringVectorCollection()
    {
        using var harness = new TestHarness();
        using var collection = harness.Store.GetDynamicCollection("memory", Definition(typeof(string)));
        await collection.UpsertAsync(Record(Guid.NewGuid(), new ReadOnlyMemory<float>(new float[] { 1, 0, 0 })));
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task MixedBatch_PreservesPrecomputedVectorsAndEmbedsOnlyTextInputs()
    {
        using var harness = new TestHarness();
        using var generator = new BatchEmbeddingGenerator();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = generator });
        using var collection = store.GetDynamicCollection("memory", Definition(typeof(string)));
        await collection.UpsertAsync(new[]
        {
            Record(Guid.NewGuid(), new float[] { 0, 1, 0 }),
            Record(Guid.NewGuid(), "embed this")
        });
        Assert.Equal("embed this", Assert.Single(generator.Inputs));
        var values = harness.Handler.Requests.Select(request =>
        {
            using var body = JsonDocument.Parse(request.Body!);
            return body.RootElement.GetProperty("fields").GetProperty("embedding").GetProperty("values")[0].GetSingle();
        });
        Assert.Equal(new float[] { 0, 1 }, values.Order());
    }

    [Fact]
    public async Task StringVectorWithoutGenerator_FailsBeforeWriting()
    {
        using var harness = new TestHarness();
        using var collection = harness.Store.GetDynamicCollection("memory", Definition(typeof(string)));
        await Assert.ThrowsAsync<NotSupportedException>(() => collection.UpsertAsync(Record(Guid.NewGuid(), "text")));
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task TypedGuidKeys_RoundTripThroughTheDocumentId()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""{"fields":{"text":"value"}}"""));
        using var collection = harness.Store.GetCollection<Guid, GuidRecord>("memory");
        var key = Guid.NewGuid();
        var record = await collection.GetAsync(key);
        Assert.NotNull(record);
        Assert.Equal(key, record.Id);
        Assert.Equal("value", record.Text);
        Assert.Empty(record.Embedding.ToArray());
    }

    [Theory]
    [InlineData("count")]
    [InlineData("dimensions")]
    [InlineData("nonfinite")]
    public async Task InvalidGeneratedBatch_IsRejectedBeforeAnyDocumentWrite(string failure)
    {
        using var harness = new TestHarness();
        using var generator = new BatchEmbeddingGenerator(failure);
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = generator });
        using var collection = store.GetDynamicCollection("memory", Definition(typeof(string)));
        await Assert.ThrowsAsync<VectorStoreException>(() => collection.UpsertAsync(new[]
        {
            Record(Guid.NewGuid(), "first"), Record(Guid.NewGuid(), "second")
        }));
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task InvalidDynamicRecords_DoNotGenerateEmbeddingsOrWrite()
    {
        using var harness = new TestHarness();
        using var generator = new BatchEmbeddingGenerator();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = generator });
        using var collection = store.GetDynamicCollection("memory", Definition(typeof(string)));
        var unknown = Record(Guid.NewGuid(), "text");
        unknown["unknown"] = "would be lost";
        await Assert.ThrowsAsync<ArgumentException>(() => collection.UpsertAsync(unknown));
        var wrongType = Record(Guid.NewGuid(), "text");
        wrongType["UserId"] = 42;
        await Assert.ThrowsAsync<ArgumentException>(() => collection.UpsertAsync(wrongType));
        Assert.Equal(0, generator.Calls);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task DynamicIndexerFilters_UseDeclaredTypesStorageNamesAndCapturedValues()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""{"root":{"children":[]}}"""));
        using var generator = new BatchEmbeddingGenerator();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with
        {
            EmbeddingGenerator = generator,
            QueryEmbeddingPrefix = "search_query: "
        });
        using var collection = store.GetDynamicCollection("memory", Definition(typeof(string)));
        var userId = "a\"b\\c";
        await collection.SearchAsync("preference", 2, new()
        {
            Filter = record => (string?)record["UserId"] == userId,
            VectorProperty = record => record["Vector"]
        }).Collect();
        Assert.Equal("search_query: preference", Assert.Single(generator.Inputs));
        using var query = JsonDocument.Parse(Assert.Single(harness.Handler.Requests).Body!);
        var yql = query.RootElement.GetProperty("yql").GetString()!;
        Assert.Contains("user_id contains", yql);
        Assert.Contains(JsonSerializer.Serialize(userId), yql);
    }

    [Fact]
    public async Task IncorrectDictionaryCast_IsRejectedBeforeEmbeddingIO()
    {
        using var harness = new TestHarness();
        using var generator = new BatchEmbeddingGenerator();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = generator });
        using var collection = store.GetDynamicCollection("memory", Definition(typeof(string)));
        await Assert.ThrowsAsync<NotSupportedException>(() => collection.SearchAsync("question", 1,
            new() { Filter = record => (int)record["UserId"]! > 1 }).Collect());
        Assert.Equal(0, generator.Calls);
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task EnsureCollectionExists_ValidatesOnceOnTheSearchEndpoint()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""{"root":{"children":[]}}"""));
        using var collection = harness.Store.GetDynamicCollection("memory", Definition());
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => collection.EnsureCollectionExistsAsync()));
        var request = Assert.Single(harness.Handler.Requests);
        Assert.Equal("/search/", request.Uri.AbsolutePath);
        using var query = JsonDocument.Parse(request.Body!);
        Assert.Equal("select documentid from sources memory where true", query.RootElement.GetProperty("yql").GetString());
        Assert.Equal(0, query.RootElement.GetProperty("hits").GetInt32());
    }

    [Fact]
    public async Task FailedSchemaValidation_IsNotCachedAsSuccess()
    {
        var calls = 0;
        using var harness = new TestHarness((_, _) => ++calls == 1
            ? TestHarness.Json("""{"root":{"errors":[{"code":4,"message":"Unknown schema memory"}]}}""")
            : TestHarness.Json("""{"root":{"children":[]}}"""));
        using var collection = harness.Store.GetDynamicCollection("memory", Definition());
        await Assert.ThrowsAsync<VectorStoreException>(() => collection.EnsureCollectionExistsAsync());
        await collection.EnsureCollectionExistsAsync();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancelledDynamicWriteAndSchemaValidation_DoNotPerformIO()
    {
        using var harness = new TestHarness();
        using var collection = harness.Store.GetDynamicCollection("memory", Definition());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collection.EnsureCollectionExistsAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collection.UpsertAsync(Record(Guid.NewGuid(), new float[] { 1, 0, 0 }), cancellation.Token));
        Assert.Empty(harness.Handler.Requests);
    }

    internal static VectorStoreCollectionDefinition Definition(Type? vectorType = null) => new()
    {
        Properties =
        [
            new VectorStoreKeyProperty("Key", typeof(Guid)),
            new VectorStoreDataProperty("UserId", typeof(string)) { StorageName = "user_id", IsIndexed = true },
            new VectorStoreDataProperty("Content", typeof(string)) { StorageName = "content" },
            new VectorStoreVectorProperty("Vector", vectorType ?? typeof(ReadOnlyMemory<float>), 3) { StorageName = "embedding" }
        ]
    };

    private static Dictionary<string, object?> Record(Guid key, object vector) => new()
    {
        ["Key"] = key,
        ["UserId"] = "alice",
        ["Content"] = "Saved preference",
        ["Vector"] = vector
    };
}

internal sealed class BatchEmbeddingGenerator(string? failure = null) : IEmbeddingGenerator<string, Embedding<float>>
{
    internal int Calls { get; private set; }
    internal List<string> Inputs { get; } = [];
    internal List<int> BatchSizes { get; } = [];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        var batch = values.ToArray();
        BatchSizes.Add(batch.Length);
        Inputs.AddRange(batch);
        GeneratedEmbeddings<Embedding<float>> result = [];
        foreach (var value in batch)
            result.Add(new Embedding<float>(failure switch
            {
                "dimensions" => new float[] { 1, 0 },
                "nonfinite" => new float[] { float.NaN, 0, 0 },
                _ => new float[] { 1, 0, 0 }
            }));
        if (failure == "count")
            result.RemoveAt(result.Count - 1);
        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}

public sealed class GuidRecord
{
    [VectorStoreKey]
    public Guid Id { get; set; }
    [VectorStoreData(StorageName = "text")]
    public string Text { get; set; } = "";
    [VectorStoreVector(3, StorageName = "embedding")]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
