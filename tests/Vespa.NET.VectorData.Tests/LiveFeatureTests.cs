using System.IO.Compression;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Vespa.Testcontainers;
using Xunit;
using Xunit.Abstractions;

namespace Vespa.VectorData.Tests;

public sealed class LiveFeatureTests(ITestOutputHelper output)
{
    [VespaIntegrationFact]
    public async Task ExpandedFeatures_MatchRealVespaSemantics()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = timeout.Token;
        await using var container = new VespaContainer(Environment.GetEnvironmentVariable("VESPA_VECTOR_DATA_IMAGE") ?? VespaContainer.DefaultImage);
        await container.StartAsync(ct);
        using var client = container.CreateClient("test");
        await DeployAsync(client, ct);
        while (!await client.IsReadyAsync(ct))
            await Task.Delay(250, ct);
        using var generator = new FeatureEmbeddingGenerator();
        var options = TestHarness.Options with
        {
            EmbeddingGenerator = generator,
            HybridRankProfile = "hybrid",
            HybridVectorTargetHits = 10,
            HybridKeywordTargetHits = 10,
            CollectionCatalog = new FixtureCatalog(),
            VectorFields = new Dictionary<string, VespaVectorFieldOptions>
            {
                [nameof(FeatureRecord.Primary)] = new() { RankProfile = "primary_search", QueryTensorName = "primary_query" },
                [nameof(FeatureRecord.Secondary)] = new() { RankProfile = "secondary_search", QueryTensorName = "secondary_query" },
                [nameof(FeatureRecord.Euclidean)] = new() { RankProfile = "euclidean_search", QueryTensorName = "metric_query" },
                [nameof(FeatureRecord.Dot)] = new() { RankProfile = "dot_search", QueryTensorName = "metric_query" }
            }
        };
        using var store = new VespaVectorStore(client, options);
        using var records = store.GetCollection<long, FeatureRecord>("features");
        Assert.True(await records.CollectionExistsAsync(ct));
        Assert.True(await store.CollectionExistsAsync("empty_features", ct));
        Assert.False(await store.CollectionExistsAsync("default", ct));
        try
        {
            Assert.False(await store.CollectionExistsAsync("absent_features", ct));
        }
        catch (VectorStoreException error)
        {
            output.WriteLine(error.ToString());
            throw;
        }

        var date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)).AddTicks(1234);
        await records.UpsertAsync([
            Make(1, "ordinary", "Docs", ["Alpha", "beta"], 3, date, [1, 0, 0], [0, 1], [0, 0, 0], [2, 0, 0]),
            Make(2, "nebula", "Docs", ["beta"], 1, date.AddDays(1), [0, 1, 0], [1, 0], [1, 0, 0], [5, 0, 0]),
            Make(3, "other", "docs", ["alpha"], 2, date.AddDays(2), [-1, 0, 0], [-1, 0], [3, 0, 0], [-1, 0, 0]),
            Make(4, "nebula", "Denied", ["Alpha"], 4, date.AddDays(3), [0, 0, 1], [0, -1], [0, 1, 0], [0, 1, 0])
        ], ct);
        List<FeatureRecord> visible;
        do
        {
            visible = await records.GetAsync(row => true, 10, cancellationToken: ct).Collect();
            if (visible.Count != 4)
                await Task.Delay(100, ct);
        } while (visible.Count != 4);
        var loaded = await records.GetAsync(1, new() { IncludeVectors = true }, ct);
        Assert.NotNull(loaded);
        Assert.Equal(date, loaded.Created);
        Assert.Equal(TimeSpan.Zero, loaded.Created.Offset);
        Assert.Equal(new[] { "Alpha", "beta" }, loaded.Tags);
        Assert.Equal(new float[] { 0, 1 }, loaded.Secondary.Vector.ToArray());
        Assert.Equal(new float[] { 0, 0, 0 }, loaded.Euclidean);

        string[] allowed = ["Docs"];
        var filtered = await records.GetAsync(row => allowed.Contains(row.Category) && row.Tags.Contains("Alpha"), 10, cancellationToken: ct).Collect();
        Assert.Equal(1, Assert.Single(filtered).Id);
        int[] priorities = [1, 3];
        Assert.Equal(new long[] { 1, 2 }, (await records.GetAsync(row => priorities.Contains(row.Priority), 10, cancellationToken: ct).Collect()).Select(row => row.Id).Order());
        Assert.Equal(new long[] { 2, 3, 4 }, (await records.GetAsync(row => row.Created > date, 10, cancellationToken: ct).Collect()).Select(row => row.Id).Order());
        var ordered = await records.GetAsync(row => true, 3, new()
        {
            OrderBy = order => order.Descending(row => row.Priority).Ascending(row => row.Category),
            Skip = 1
        }, ct).Collect();
        Assert.Equal(new long[] { 1, 3, 2 }, ordered.Select(row => row.Id));
        Assert.Equal(1, Assert.Single(await records.SearchAsync(new Embedding<float>(new float[] { 1, 0, 0 }), 1,
            new() { VectorProperty = row => row.Primary }, ct).Collect()).Record.Id);
        Assert.Equal(2, Assert.Single(await records.SearchAsync(new float[] { 1, 0 }, 1,
            new() { VectorProperty = row => row.Secondary, IncludeVectors = true }, ct).Collect()).Record.Id);

        await CheckMetricAsync(client, DistanceFunction.CosineSimilarity, "primary", "primary_search", "primary_query", [1, 0, 0],
            [1, 0, 0, -1], 1, ct);
        await CheckMetricAsync(client, DistanceFunction.CosineDistance, "primary", "primary_search", "primary_query", [1, 0, 0],
            [0, 1, 1, 2], 0, ct);
        await CheckMetricAsync(client, DistanceFunction.EuclideanDistance, "euclidean", "euclidean_search", "metric_query", [0, 0, 0],
            [0, 1, 1, 3], 0, ct);
        await CheckMetricAsync(client, DistanceFunction.EuclideanSquaredDistance, "euclidean", "euclidean_search", "metric_query", [0, 0, 0],
            [0, 1, 1, 9], 0, ct);
        await CheckMetricAsync(client, DistanceFunction.DotProductSimilarity, "dot", "dot_search", "metric_query", [1, 0, 0],
            [5, 2, 0, -1], 5, ct);
        await CheckMetricAsync(client, DistanceFunction.NegativeDotProductSimilarity, "dot", "dot_search", "metric_query", [1, 0, 0],
            [-5, -2, 0, 1], -5, ct);
        await CheckRoundedEuclideanBoundaryAsync(client, ct);

        await records.UpsertAsync(Make(5, "ordinary", "Docs", [], 5, date, [1, 0.01f, 0], [1, 0], [1, 0, 0], [1, 0, 0]), ct);
        while ((await records.GetAsync(row => row.Priority == 5, 1, cancellationToken: ct).Collect()).Count == 0)
            await Task.Delay(100, ct);
        using var hybridStore = new VespaVectorStore(client, options with { HybridVectorTargetHits = 2, HybridKeywordTargetHits = 3 });
        using var hybridCollection = hybridStore.GetCollection<long, FeatureRecord>("features");
        var hybrid = Assert.IsAssignableFrom<IKeywordHybridSearchable<FeatureRecord>>(hybridCollection);
        // Dense alone returns 1 and 5; the lexical branch must contribute 2.
        Assert.Equal(new long[] { 1, 5 }, (await records.SearchAsync(new float[] { 1, 0, 0 }, 2,
            new() { VectorProperty = row => row.Primary, Filter = row => row.Category == "Docs" }, ct).Collect()).Select(hit => hit.Record.Id));
        var combined = await hybrid.HybridSearchAsync(new float[] { 1, 0, 0 }, ["nebula"], 2, new()
        {
            VectorProperty = row => row.Primary,
            AdditionalProperty = row => row.Title,
            Filter = row => row.Category == "Docs"
        }, ct).Collect();
        Assert.Equal(new long[] { 1, 2 }, combined.Select(hit => hit.Record.Id).Order());
        Assert.All(combined, hit => Assert.True(double.IsFinite(hit.Score!.Value)));
        Assert.Equal(combined.Select(hit => hit.Score).OrderDescending(), combined.Select(hit => hit.Score));
        Assert.Empty(await hybrid.HybridSearchAsync(new float[] { 1, 0, 0 }, ["nebula\" or true"], 10, new()
        {
            VectorProperty = row => row.Primary,
            Filter = row => row.Category == "nobody"
        }, ct).Collect());

        using var optionalStore = new VespaVectorStore(client, TestHarness.Options with { EmbeddingGenerator = generator });
        using var optional = optionalStore.GetCollection<string, OptionalFeatureRecord>("empty_features");
        await optional.UpsertAsync(new OptionalFeatureRecord { Id = "optional", TextInput = "source text", Optional = null }, ct);
        var optionalRead = await optional.GetAsync("optional", new() { IncludeVectors = true }, ct);
        Assert.NotNull(optionalRead);
        Assert.Null(optionalRead.TextInput);
        Assert.Null(optionalRead.Optional);
        Assert.True(generator.Inputs > 0);
        output.WriteLine("Verified existence, six metrics and thresholds, multi-vector search, hybrid filters, ordering, memberships, dates, lists, nullable and typed-string embeddings against real Vespa.");
    }

    private static FeatureRecord Make(long id, string title, string category, List<string> tags, int priority,
        DateTimeOffset created, float[] primary, float[] secondary, float[] euclidean, float[] dot) => new()
    {
        Id = id, Title = title, Category = category, Tags = tags, Priority = priority, Created = created,
        Primary = primary, Secondary = new(secondary), Euclidean = euclidean, Dot = dot
    };

    private static async Task CheckMetricAsync(VespaClient client, string metric, string field, string profile, string queryName,
        float[] query, double[] expected, double threshold, CancellationToken ct)
    {
        foreach (var approximate in new[] { false, true })
        {
            using var store = new VespaVectorStore(client, TestHarness.Options with
            {
                RankProfile = profile, QueryTensorName = queryName, Approximate = approximate
            });
            using var collection = store.GetDynamicCollection("features", new()
            {
                Properties = [new VectorStoreKeyProperty("Id", typeof(long)),
                    new VectorStoreVectorProperty("Vector", typeof(ReadOnlyMemory<float>), 3) { StorageName = field, DistanceFunction = metric }]
            });
            var results = await collection.SearchAsync(query, 4, cancellationToken: ct).Collect();
            Assert.Equal(expected.Length, results.Count);
            for (var i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], results[i].Score!.Value, 5);
            Assert.Single(await collection.SearchAsync(query, 4, new() { ScoreThreshold = threshold }, ct).Collect());
            if (metric is DistanceFunction.DotProductSimilarity or DistanceFunction.NegativeDotProductSimilarity)
            {
                var sign = metric == DistanceFunction.DotProductSimilarity ? 1 : -1;
                var page = await collection.SearchAsync(query, 3, new() { ScoreThreshold = sign * 2, Skip = 1 }, ct).Collect();
                Assert.Equal(sign * 2, Assert.Single(page).Score!.Value, 5);
                Assert.Equal(4, (await collection.SearchAsync(query, 4, new() { ScoreThreshold = sign * -1 }, ct).Collect()).Count);
                Assert.Equal(3, (await collection.SearchAsync(query, 4, new() { ScoreThreshold = 0 }, ct).Collect()).Count);
            }
        }
    }

    private static async Task CheckRoundedEuclideanBoundaryAsync(VespaClient client, CancellationToken ct)
    {
        var definition = new VectorStoreCollectionDefinition
        {
            Properties = [new VectorStoreKeyProperty("Id", typeof(string)),
                new VectorStoreVectorProperty("Vector", typeof(ReadOnlyMemory<float>), 3)
                { StorageName = "euclidean", DistanceFunction = DistanceFunction.EuclideanDistance }]
        };
        using (var store = new VespaVectorStore(client, TestHarness.Options))
        using (var collection = store.GetDynamicCollection("tiny_features", definition))
            await collection.UpsertAsync(new Dictionary<string, object?>
            {
                ["Id"] = "tiny", ["Vector"] = new float[] { 1e-17f, 0, 0 }
            }, ct);
        foreach (var approximate in new[] { false, true })
        foreach (var metric in new[] { DistanceFunction.EuclideanDistance, DistanceFunction.EuclideanSquaredDistance })
        {
            definition.Properties.OfType<VectorStoreVectorProperty>().Single().DistanceFunction = metric;
            using var store = new VespaVectorStore(client, TestHarness.Options with { RankProfile = "tiny_search", Approximate = approximate });
            using var collection = store.GetDynamicCollection("tiny_features", definition);
            List<VectorSearchResult<Dictionary<string, object?>>> hits;
            do
            {
                hits = await collection.SearchAsync(new float[] { 0, 0, 0 }, 1, cancellationToken: ct).Collect();
                if (hits.Count == 0)
                    await Task.Delay(100, ct);
            } while (hits.Count == 0);
            Assert.Equal("tiny", Assert.Single(hits).Record["Id"]);
            // Closeness rounds to 1, so Vespa's public distance is 0 although
            // the stored point differs from zero. A raw threshold of 0 loses it.
            Assert.Equal(0, hits[0].Score);
            Assert.Single(await collection.SearchAsync(new float[] { 0, 0, 0 }, 1, new() { ScoreThreshold = 0 }, ct).Collect());
        }
    }

    private static async Task DeployAsync(VespaClient client, CancellationToken ct)
    {
        using var package = new MemoryStream();
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteAsync(archive, "services.xml", """
                <services version="1.0"><container id="default" version="1.0"><search/><document-api/></container>
                <content id="features" version="1.0"><redundancy>1</redundancy><documents>
                <document type="features" mode="index"/><document type="empty_features" mode="index"/>
                <document type="tiny_features" mode="index"/>
                </documents><nodes><node distribution-key="0" hostalias="node1"/></nodes>
                <tuning><resource-limits><disk>0.95</disk></resource-limits></tuning></content></services>
                """);
            await WriteAsync(archive, "hosts.xml", "<hosts><host name=\"localhost\"><alias>node1</alias></host></hosts>");
            await WriteAsync(archive, "schemas/features.sd", """
                schema features {
                    document features {
                        field title type string {
                            indexing: summary | index
                            index: enable-bm25
                        }
                        field category type string {
                            indexing: attribute | summary
                            match: cased
                        }
                        field tags type array<string> {
                            indexing: attribute | summary
                            match: cased
                        }
                        field priority type int {
                            indexing: attribute | summary
                        }
                        field created type long {
                            indexing: attribute | summary
                        }
                        field primary type tensor<float>(x[3]) {
                            indexing: attribute | index | summary
                            attribute { distance-metric: angular }
                        }
                        field secondary type tensor<float>(x[2]) {
                            indexing: attribute | index | summary
                            attribute { distance-metric: angular }
                        }
                        field euclidean type tensor<float>(x[3]) {
                            indexing: attribute | index | summary
                            attribute { distance-metric: euclidean }
                        }
                        field dot type tensor<float>(x[3]) {
                            indexing: attribute | index | summary
                            attribute { distance-metric: dotproduct }
                        }
                    }
                    rank-profile primary_search { inputs { query(primary_query) tensor<float>(x[3]) }
                        first-phase { expression: closeness(field, primary) }
                        match-features: distance(field, primary)
                    }
                    rank-profile secondary_search { inputs { query(secondary_query) tensor<float>(x[2]) }
                        first-phase { expression: closeness(field, secondary) }
                        match-features: distance(field, secondary)
                    }
                    rank-profile euclidean_search { inputs { query(metric_query) tensor<float>(x[3]) }
                        first-phase { expression: -distance(field, euclidean) }
                        match-features: distance(field, euclidean)
                    }
                    rank-profile dot_search { inputs { query(metric_query) tensor<float>(x[3]) }
                        first-phase { expression: -distance(field, dot) }
                        match-features: distance(field, dot)
                    }
                    rank-profile hybrid { inputs { query(primary_query) tensor<float>(x[3]) }
                        first-phase { expression: 10 * bm25(title) + closeness(field, primary) } }
                }
                """);
            await WriteAsync(archive, "schemas/empty_features.sd", """
                schema empty_features { document empty_features {
                    field text_embedding type tensor<float>(x[3]) {
                        indexing: attribute | summary
                        attribute { distance-metric: angular }
                    }
                    field optional type tensor<float>(x[3]) {
                        indexing: attribute | summary
                        attribute { distance-metric: angular }
                    }
                } }
                """);
            await WriteAsync(archive, "schemas/tiny_features.sd", """
                schema tiny_features {
                    document tiny_features {
                        field euclidean type tensor<float>(x[3]) {
                            indexing: attribute | index | summary
                            attribute { distance-metric: euclidean }
                        }
                    }
                    rank-profile tiny_search {
                        inputs { query(query_embedding) tensor<float>(x[3]) }
                        first-phase { expression: closeness(field, euclidean) }
                        match-features: distance(field, euclidean)
                    }
                }
                """);
        }
        package.Position = 0;
        await client.Admin.DeployAsync(package, cancellationToken: ct);
    }

    private static async Task WriteAsync(ZipArchive archive, string path, string content)
    {
        await using var stream = archive.CreateEntry(path).Open();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
    }

    // This fixture owns and just deployed the complete package above.
    private sealed class FixtureCatalog : IVespaCollectionCatalog
    {
        public Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(name is "features" or "empty_features" or "tiny_features");
        public async IAsyncEnumerable<string> ListCollectionNamesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield return "features";
            yield return "empty_features";
            yield return "tiny_features";
        }
    }
}

public sealed class FeatureRecord
{
    [VectorStoreKey] public long Id { get; set; }
    [VectorStoreData(StorageName = "title", IsFullTextIndexed = true)] public string Title { get; set; } = "";
    [VectorStoreData(StorageName = "category", IsIndexed = true)] public string Category { get; set; } = "";
    [VectorStoreData(StorageName = "tags", IsIndexed = true)] public List<string> Tags { get; set; } = [];
    [VectorStoreData(StorageName = "priority", IsIndexed = true)] public int Priority { get; set; }
    [VectorStoreData(StorageName = "created", IsIndexed = true)] public DateTimeOffset Created { get; set; }
    [VectorStoreVector(3, StorageName = "primary")] public ReadOnlyMemory<float> Primary { get; set; }
    [VectorStoreVector(2, StorageName = "secondary")] public Embedding<float> Secondary { get; set; } = new(new float[] { 1, 0 });
    [VectorStoreVector(3, StorageName = "euclidean", DistanceFunction = DistanceFunction.EuclideanDistance)] public float[] Euclidean { get; set; } = [];
    [VectorStoreVector(3, StorageName = "dot", DistanceFunction = DistanceFunction.DotProductSimilarity)] public float[] Dot { get; set; } = [];
}

public sealed class OptionalFeatureRecord
{
    [VectorStoreKey] public string Id { get; set; } = "";
    [VectorStoreVector(3, StorageName = "text_embedding")] public string TextInput { get; set; } = "initializer";
    [VectorStoreVector(3, StorageName = "optional")] public ReadOnlyMemory<float>? Optional { get; set; }
}

internal sealed class FeatureEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    internal int Inputs { get; private set; }
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var inputs = values.ToArray();
        Inputs += inputs.Length;
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(inputs.Select(_ => new Embedding<float>(new float[] { 1, 0, 0 }))));
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
