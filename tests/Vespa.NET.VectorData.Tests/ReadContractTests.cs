using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.VectorData;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class ReadContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedVector_IsAssignedOnceWithoutAnIntermediateNull(bool search)
    {
        using var harness = new TestHarness((request, _) => TestHarness.Json(request.Method == HttpMethod.Get
            ? """{"fields":{"embedding":{"values":[1,0,0]}}}""" : TestHarness.Hit()));
        using var collection = harness.Store.GetCollection<string, GuardedVectorRecord>("article");
        var record = search
            ? Assert.Single(await collection.SearchAsync(new float[] { 1, 0, 0 }, 1, new() { IncludeVectors = true }).Collect()).Record
            : await collection.GetAsync("a:1", new() { IncludeVectors = true });
        Assert.NotNull(record);
        Assert.Equal(new float[] { 1, 0, 0 }, record.Embedding);
        Assert.Equal(1, record.VectorAssignments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrNullRequestedVector_FailsForSearchFilteredAndKeyReads(bool nullValue)
    {
        var response = JsonNode.Parse(TestHarness.Hit())!;
        var fields = response["root"]!["children"]![0]!["fields"]!.AsObject();
        if (nullValue)
            fields["embedding"] = null;
        else
            fields.Remove("embedding");
        using var harness = new TestHarness((request, _) => TestHarness.Json(request.Method == HttpMethod.Get
            ? JsonSerializer.Serialize(new { fields }) : response.ToJsonString()));
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { VectorSummaryClass = "vector_data" });
        using var collection = store.GetCollection<string, Article>("article");
        var error = await Assert.ThrowsAsync<VectorStoreException>(() => collection.SearchAsync(new float[] { 1, 0, 0 }, 1,
            new() { IncludeVectors = true }).Collect());
        Assert.Contains("embedding", error.Message);
        Assert.Contains("vector_data", error.Message);
        await Assert.ThrowsAsync<VectorStoreException>(() => collection.GetAsync(a => a.Published, 1,
            new() { IncludeVectors = true }).Collect());
        await Assert.ThrowsAsync<VectorStoreException>(() => collection.GetAsync("a:1", new() { IncludeVectors = true }));
        Assert.Single(await collection.SearchAsync(new float[] { 1, 0, 0 }, 1).Collect());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyReads_RequestOnlyMappedFieldsAndIncludeVectorWhenRequested(bool includeVectors)
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""
            {"fields":{"title":"Stored","embedding":{"values":[1,0,0]}}}
            """));
        var result = await harness.Collection.GetAsync("a:1", new() { IncludeVectors = includeVectors });
        Assert.Equal("Stored", result!.Title);
        var query = Uri.UnescapeDataString(Assert.Single(harness.Handler.Requests).Uri.Query);
        Assert.Contains("fieldSet=article:title,category,priority,published" + (includeVectors ? ",embedding" : ""), query);
        Assert.Equal(includeVectors ? 3 : 0, result.Embedding.Length);
    }

    [Fact]
    public async Task ExplicitNullSummary_SelectsBackendDefault()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit()));
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { SummaryClass = null });
        await store.GetCollection<string, Article>("article").SearchAsync(new float[] { 1, 0, 0 }, 1).Collect();
        using var body = JsonDocument.Parse(Assert.Single(harness.Handler.Requests).Body!);
        Assert.False(body.RootElement.TryGetProperty("presentation.summary", out _));
    }

    [Fact]
    public async Task RequiredFields_AreEnforcedWithoutRejectingUnsetOptionalFields()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""{"fields":{"title":"Stored"}}"""));
        using var collection = harness.Store.GetCollection<string, RequiredRecord>("article");
        Assert.Equal("Stored", (await collection.GetAsync("a"))!.Title);
        using var strict = new VespaVectorStore(harness.Client, TestHarness.Options with { RequiredDataFields = ["category"] });
        var error = await Assert.ThrowsAsync<VectorStoreException>(() => strict.GetCollection<string, Article>("article").GetAsync("a"));
        Assert.Contains("category", error.Message);
        using var missing = new TestHarness((_, _) => TestHarness.Json("""{"fields":{}}"""));
        await Assert.ThrowsAsync<VectorStoreException>(() => missing.Store.GetCollection<string, RequiredRecord>("article").GetAsync("a"));
        await Assert.ThrowsAsync<ArgumentException>(() => collection.UpsertAsync(new RequiredRecord
        {
            Id = "a", Title = null!, Embedding = new float[] { 1, 0, 0 }
        }));
    }

    [Fact]
    public void RequiredFieldConfiguration_RejectsUnmappedOrVectorFields()
    {
        using var harness = new TestHarness();
        foreach (var field in new[] { "missing", "embedding" })
        {
            using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { RequiredDataFields = [field] });
            Assert.Throws<ArgumentException>(() => store.GetCollection<string, Article>("article"));
        }
    }

    [Fact]
    public async Task KeyAndVectorOnlyRecords_CanReadIdentityWithoutFetchingFields()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json("""{"id":"id:test:article::a"}"""));
        var definition = new VectorStoreCollectionDefinition
        {
            Properties = [new VectorStoreKeyProperty(nameof(Article.Id), typeof(string)),
                new VectorStoreVectorProperty(nameof(Article.Embedding), typeof(ReadOnlyMemory<float>), 3) { StorageName = "embedding" }]
        };
        using var collection = harness.Store.GetCollection<string, Article>("article", definition);
        Assert.Equal("a", (await collection.GetAsync("a"))!.Id);
        Assert.Contains("fieldSet=[id]", Uri.UnescapeDataString(Assert.Single(harness.Handler.Requests).Uri.Query));
    }
}

public sealed class RequiredRecord
{
    [VectorStoreKey] public string Id { get; set; } = "";
    [VectorStoreData(StorageName = "title")] public required string Title { get; set; }
    [VectorStoreData(StorageName = "category")] public string? Category { get; set; }
    [VectorStoreVector(3, StorageName = "embedding")] public ReadOnlyMemory<float> Embedding { get; set; }
}

public sealed class GuardedVectorRecord
{
    private float[] _embedding = [];
    [VectorStoreKey] public string Id { get; set; } = "";
    public int VectorAssignments { get; private set; }
    [VectorStoreVector(3, StorageName = "embedding")]
    public float[] Embedding
    {
        get => _embedding;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _embedding = value;
            VectorAssignments++;
        }
    }
}
