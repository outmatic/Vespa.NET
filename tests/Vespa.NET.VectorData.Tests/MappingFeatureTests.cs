using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class MappingFeatureTests
{
    [Fact]
    public async Task CollectionsWithoutVectors_SupportDocumentOperations()
    {
        using var harness = new TestHarness((request, _) => TestHarness.Json(request.Method == HttpMethod.Get
            ? """{"fields":{"text":"stored"}}""" : "{}"));
        using var collection = harness.Store.GetCollection<long, DataOnlyRecord<long>>("notes");
        await collection.UpsertAsync(new DataOnlyRecord<long> { Id = -42, Text = "stored" });
        var read = await collection.GetAsync(-42, new() { IncludeVectors = true });
        Assert.Equal(-42, read!.Id);
        Assert.Equal("stored", read.Text);
        await collection.DeleteAsync(-42);
        Assert.Equal(3, harness.Handler.Requests.Count);
        await Assert.ThrowsAsync<NotSupportedException>(() => collection.SearchAsync(new float[] { 1, 0 }, 1).Collect());
        Assert.Equal(3, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task IntegerKeys_RoundTripWithoutPrecisionLoss()
    {
        await CheckKey(int.MinValue);
        await CheckKey(int.MaxValue);
        await CheckKey(long.MinValue);
        await CheckKey(long.MaxValue);
        await CheckKey(ulong.MaxValue);

        static async Task CheckKey<TKey>(TKey key) where TKey : notnull
        {
            using var harness = new TestHarness((request, _) => TestHarness.Json(request.Method == HttpMethod.Get
                ? """{"fields":{"text":"stored"}}""" : "{}"));
            using var collection = harness.Store.GetCollection<TKey, DataOnlyRecord<TKey>>("notes");
            await collection.UpsertAsync(new DataOnlyRecord<TKey> { Id = key, Text = "stored" });
            var record = await collection.GetAsync(key);
            Assert.Equal(key, record!.Id);
            var encoded = ((IFormattable)key).ToString(null, CultureInfo.InvariantCulture);
            Assert.All(harness.Handler.Requests, request => Assert.EndsWith("/docid/" + encoded, request.Uri.AbsolutePath));
        }
    }

    [Theory]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData("-0")]
    [InlineData("9223372036854775808")]
    public async Task NoncanonicalOrOverflowingIntegerKeys_AreRejectedOnFilteredReads(string id)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(JsonSerializer.Serialize(new
        {
            root = new { children = new[] { new { fields = new { documentid = "id:test:notes::" + id, text = "stored" } } } }
        })));
        using var collection = harness.Store.GetCollection<long, DataOnlyRecord<long>>("notes");
        await Assert.ThrowsAsync<VectorStoreException>(() => collection.GetAsync(_ => true, 1).Collect());
    }

    [Fact]
    public async Task MultipleNativeVectors_RoundTripAllFieldsWithIndependentDimensions()
    {
        using var harness = new TestHarness((request, _) => TestHarness.Json(request.Method == HttpMethod.Get
            ? """{"fields":{"short_vector":{"values":[1,0]},"long_vector":{"values":[0,1,0]}}}""" : "{}"));
        using var collection = harness.Store.GetCollection<string, MultipleVectorRecord>("vectors");
        await collection.UpsertAsync(new MultipleVectorRecord
        {
            Id = "a", Short = new Embedding<float>(new float[] { 1, 0 }), Long = new float[] { 0, 1, 0 }
        });
        var read = await collection.GetAsync("a", new() { IncludeVectors = true });
        Assert.Equal(new float[] { 1, 0 }, read!.Short.Vector.ToArray());
        Assert.Equal(new float[] { 0, 1, 0 }, read.Long.ToArray());
        using var body = JsonDocument.Parse(harness.Handler.Requests[0].Body!);
        var fields = body.RootElement.GetProperty("fields");
        Assert.Equal(2, fields.GetProperty("short_vector").GetProperty("values").GetArrayLength());
        Assert.Equal(3, fields.GetProperty("long_vector").GetProperty("values").GetArrayLength());
        Assert.Contains("short_vector,long_vector", Uri.UnescapeDataString(harness.Handler.Requests[1].Uri.Query));
        await Assert.ThrowsAsync<ArgumentException>(() => collection.UpsertAsync(new MultipleVectorRecord
        {
            Id = "b", Short = new Embedding<float>(new float[] { 1, 0, 0 }), Long = new float[] { 0, 1, 0 }
        }));
        Assert.Equal(2, harness.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullableVectors_AcceptAbsenceAndClearInitializers(bool includeVectors)
    {
        using var harness = new TestHarness((request, _) => TestHarness.Json(request.Method == HttpMethod.Get
            ? """{"fields":{}}""" : "{}"));
        using var collection = harness.Store.GetCollection<string, NullableVectorsRecord>("vectors");
        await collection.UpsertAsync(new NullableVectorsRecord { Id = "a", Array = null, Memory = null, Embedding = null });
        using var body = JsonDocument.Parse(harness.Handler.Requests[0].Body!);
        Assert.Empty(body.RootElement.GetProperty("fields").EnumerateObject());
        var read = await collection.GetAsync("a", new() { IncludeVectors = includeVectors });
        Assert.Null(read!.Array);
        Assert.Null(read.Memory);
        Assert.Null(read.Embedding);
    }

    [Fact]
    public async Task RequiredNullableVector_StillRejectsMissingValues()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""{"fields":{}}"""));
        using var collection = harness.Store.GetCollection<string, RequiredNullableVectorRecord>("vectors");
        await Assert.ThrowsAsync<ArgumentException>(() => collection.UpsertAsync(new RequiredNullableVectorRecord { Id = "a", Vector = null }));
        Assert.Empty(harness.Handler.Requests);
        await Assert.ThrowsAsync<VectorStoreException>(() => collection.GetAsync("a", new() { IncludeVectors = true }));
    }

    [Fact]
    public async Task NullableVectors_WithValuesRetainAllSupportedRepresentations()
    {
        using var harness = new TestHarness((request, _) => TestHarness.Json(request.Method == HttpMethod.Get
            ? """{"fields":{"array":{"values":[1,0,0]},"memory":{"values":[0,1,0]},"embedding":{"values":[0,0,1]}}}""" : "{}"));
        using var collection = harness.Store.GetCollection<string, NullableVectorsRecord>("vectors");
        var read = await collection.GetAsync("a", new() { IncludeVectors = true });
        Assert.Equal(new float[] { 1, 0, 0 }, read!.Array);
        Assert.Equal(new float[] { 0, 1, 0 }, read.Memory!.Value.ToArray());
        Assert.Equal(new float[] { 0, 0, 1 }, read.Embedding!.Vector.ToArray());
        await collection.UpsertAsync(read);
        using var body = JsonDocument.Parse(harness.Handler.Requests[1].Body!);
        Assert.Equal(3, body.RootElement.GetProperty("fields").EnumerateObject().Count());
    }

    [Fact]
    public async Task DynamicReferenceVectors_WithoutNullabilityMetadataRemainRequired()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""{"fields":{}}"""));
        using var collection = harness.Store.GetDynamicCollection("vectors", DynamicCollectionTests.Definition(typeof(float[])));
        await Assert.ThrowsAsync<ArgumentException>(() => collection.UpsertAsync(new Dictionary<string, object?>
        {
            ["Key"] = Guid.NewGuid(), ["Vector"] = null
        }));
        Assert.Empty(harness.Handler.Requests);
        await Assert.ThrowsAsync<VectorStoreException>(() => collection.GetAsync(Guid.NewGuid(), new() { IncludeVectors = true }));
    }

    [Fact]
    public async Task TypedStringVectors_GenerateButNeverReturnTextOrVectorInSourceProperty()
    {
        using var generator = new BatchEmbeddingGenerator();
        using var harness = new TestHarness((request, _) => TestHarness.Json(request.Method == HttpMethod.Get
            ? """{"fields":{"text":"original","embedding":{"values":[1,0,0]}}}""" : "{}"));
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = generator });
        using var collection = store.GetCollection<string, GeneratedVectorRecord>("vectors");
        await collection.UpsertAsync(new GeneratedVectorRecord { Id = "a", Text = "original", Source = "embed this" });
        Assert.Equal("embed this", Assert.Single(generator.Inputs));
        using var body = JsonDocument.Parse(harness.Handler.Requests[0].Body!);
        Assert.Equal(3, body.RootElement.GetProperty("fields").GetProperty("embedding").GetProperty("values").GetArrayLength());
        var read = await collection.GetAsync("a", new() { IncludeVectors = true });
        Assert.Equal("original", read!.Text);
        Assert.Null(read.Source);
        Assert.DoesNotContain("embedding", harness.Handler.Requests[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void IncompatibleEmbeddingType_IsRejectedBeforeAnyIO()
    {
        using var harness = new TestHarness();
        using var generator = new BatchEmbeddingGenerator();
        var definition = DynamicCollectionTests.Definition(typeof(string));
        definition.EmbeddingGenerator = generator;
        definition.Properties.OfType<VectorStoreVectorProperty>().Single().EmbeddingType = typeof(Embedding<Half>);
        Assert.Throws<NotSupportedException>(() => harness.Store.GetDynamicCollection("vectors", definition));
        Assert.Empty(harness.Handler.Requests);
    }

    [Theory]
    [InlineData(DistanceFunction.DotProductSimilarity)]
    [InlineData(DistanceFunction.NegativeDotProductSimilarity)]
    [InlineData(DistanceFunction.EuclideanDistance)]
    [InlineData(DistanceFunction.EuclideanSquaredDistance)]
    public async Task ZeroVectors_AreAllowedForNonCosineMetrics(string distance)
    {
        using var harness = new TestHarness();
        var definition = DynamicCollectionTests.Definition();
        definition.Properties.OfType<VectorStoreVectorProperty>().Single().DistanceFunction = distance;
        using var collection = harness.Store.GetDynamicCollection("vectors", definition);
        await collection.UpsertAsync(new Dictionary<string, object?>
        {
            ["Key"] = Guid.NewGuid(), ["Vector"] = new float[] { 0, 0, 0 }
        });
        Assert.Single(harness.Handler.Requests);
    }

    [Theory]
    [InlineData(DistanceFunction.CosineSimilarity)]
    [InlineData(DistanceFunction.CosineDistance)]
    public async Task ZeroVectors_AreRejectedForCosineMetrics(string distance)
    {
        using var harness = new TestHarness();
        var definition = DynamicCollectionTests.Definition();
        definition.Properties.OfType<VectorStoreVectorProperty>().Single().DistanceFunction = distance;
        using var collection = harness.Store.GetDynamicCollection("vectors", definition);
        await Assert.ThrowsAsync<ArgumentException>(() => collection.UpsertAsync(new Dictionary<string, object?>
        {
            ["Key"] = Guid.NewGuid(), ["Vector"] = new float[] { 0, 0, 0 }
        }));
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task Lists_AreCopiedWhileEnumeratingReusedRecords()
    {
        using var harness = new TestHarness();
        using var collection = harness.Store.GetCollection<string, DateListRecord>("records");
        var record = new DateListRecord { Id = "first", Tags = ["first"], Count = [1] };
        await collection.UpsertAsync(Records());
        var written = harness.Handler.Requests.Select(request =>
        {
            using var body = JsonDocument.Parse(request.Body!);
            return (Tag: body.RootElement.GetProperty("fields").GetProperty("tags")[0].GetString(),
                Count: body.RootElement.GetProperty("fields").GetProperty("count")[0].GetInt32());
        }).OrderBy(value => value.Count).ToArray();
        Assert.Equal(new (string?, int)[] { ("first", 1), ("second", 2) }, written);

        IEnumerable<DateListRecord> Records()
        {
            yield return record;
            record.Id = "second";
            record.Tags[0] = "second";
            record.Count[0] = 2;
            yield return record;
        }
    }

    [Fact]
    public async Task Dates_RoundTripUtcTicksAndEpochDaysWithoutLosingSubmillisecondPrecision()
    {
        var utc = DateTime.UnixEpoch.AddTicks(17);
        var offset = new DateTimeOffset(utc).ToOffset(TimeSpan.FromHours(3));
        var day = new DateOnly(1969, 12, 31);
        using var harness = new TestHarness((request, _) => TestHarness.Json(request.Method == HttpMethod.Get
            ? """{"fields":{"utc":17,"offset":17,"day":-1,"dates":[17]}}""" : "{}"));
        using var collection = harness.Store.GetCollection<string, DateListRecord>("records");
        await collection.UpsertAsync(new DateListRecord { Id = "a", Utc = utc, Offset = offset, Day = day, Dates = [utc] });
        using var body = JsonDocument.Parse(harness.Handler.Requests[0].Body!);
        var fields = body.RootElement.GetProperty("fields");
        Assert.Equal(17, fields.GetProperty("utc").GetInt64());
        Assert.Equal(17, fields.GetProperty("offset").GetInt64());
        Assert.Equal(-1, fields.GetProperty("day").GetInt64());
        Assert.Equal(17, fields.GetProperty("dates")[0].GetInt64());
        var read = await collection.GetAsync("a");
        Assert.Equal(utc, read!.Utc);
        Assert.Equal(DateTimeKind.Utc, read.Utc.Kind);
        Assert.Equal(offset.UtcDateTime, read.Offset.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, read.Offset.Offset);
        Assert.Equal(day, read.Day);
        Assert.Equal(new[] { utc }, read.Dates);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task DateTimesWithoutUtcKind_AreRejectedBeforeIO(DateTimeKind kind)
    {
        using var harness = new TestHarness();
        using var collection = harness.Store.GetCollection<string, DateListRecord>("records");
        await Assert.ThrowsAsync<ArgumentException>(() => collection.UpsertAsync(new DateListRecord
        {
            Id = "a", Utc = DateTime.SpecifyKind(DateTime.UnixEpoch, kind)
        }));
        Assert.Empty(harness.Handler.Requests);
    }
}

public sealed class DataOnlyRecord<TKey> where TKey : notnull
{
    [VectorStoreKey] public TKey Id { get; set; } = default!;
    [VectorStoreData(StorageName = "text")] public string Text { get; set; } = "";
}

public sealed class MultipleVectorRecord
{
    [VectorStoreKey] public string Id { get; set; } = "";
    [VectorStoreVector(2, StorageName = "short_vector")] public Embedding<float> Short { get; set; } = null!;
    [VectorStoreVector(3, StorageName = "long_vector")] public ReadOnlyMemory<float> Long { get; set; }
}

public sealed class NullableVectorsRecord
{
    [VectorStoreKey] public string Id { get; set; } = "";
    [VectorStoreVector(3, StorageName = "array")] public float[]? Array { get; set; } = [1, 0, 0];
    [VectorStoreVector(3, StorageName = "memory")] public ReadOnlyMemory<float>? Memory { get; set; } = new float[] { 1, 0, 0 };
    [VectorStoreVector(3, StorageName = "embedding")] public Embedding<float>? Embedding { get; set; } = new(new float[] { 1, 0, 0 });
}

public sealed class RequiredNullableVectorRecord
{
    [VectorStoreKey] public string Id { get; set; } = "";
    [VectorStoreVector(3, StorageName = "embedding")] public required float[]? Vector { get; set; }
}

public sealed class GeneratedVectorRecord
{
    [VectorStoreKey] public string Id { get; set; } = "";
    [VectorStoreData(StorageName = "text")] public string Text { get; set; } = "";
    [VectorStoreVector(3, StorageName = "embedding")] public string Source { get; set; } = "initializer must not leak";
}

public sealed class DateListRecord
{
    [VectorStoreKey] public string Id { get; set; } = "";
    [VectorStoreData(StorageName = "tags", IsIndexed = true)] public List<string> Tags { get; set; } = [];
    [VectorStoreData(StorageName = "count")] public List<int> Count { get; set; } = [];
    [VectorStoreData(StorageName = "utc", IsIndexed = true)] public DateTime Utc { get; set; } = DateTime.UnixEpoch;
    [VectorStoreData(StorageName = "offset", IsIndexed = true)] public DateTimeOffset Offset { get; set; } = DateTimeOffset.UnixEpoch;
    [VectorStoreData(StorageName = "day", IsIndexed = true)] public DateOnly Day { get; set; } = DateOnly.FromDateTime(DateTime.UnixEpoch);
    [VectorStoreData(StorageName = "dates")] public List<DateTime> Dates { get; set; } = [];
}
