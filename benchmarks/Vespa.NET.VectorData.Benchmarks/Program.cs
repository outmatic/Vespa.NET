using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.VectorData;
using Vespa;
using Vespa.Feed;
using Vespa.Models;
using Vespa.VectorData;
using Vespa.VectorData.Benchmarks;

if (args is ["--self-test"])
{
    Metrics.SelfTest();
    return;
}
if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine("""
        ANN benchmark against an explicitly selected, dedicated Vespa application.
        --endpoint URL                      Required query/document endpoint.
        --deploy --config-endpoint URL      Replace the WHOLE application with the benchmark schema.
        --feed                              Write the corpus (otherwise reuse the identical corpus).
        --dataset FILE                      JSON {documents:[{id,vector,bucket,text}],queries:[[...]]}.
        --documents 10000 --dimensions 128 --queries 100 --seed 1729
                                            Synthetic clustered vectors if --dataset is absent.
        --k 10 --repeats 3 --warmup 5        Samples = queries * repeats per configuration.
        --explore 0,32,64,128                 HNSW additional hits; exact baseline always included.
        --selectivity 100,10,1               Filters bucket < value; actual counts are reported.
        --summaries vector_data,default      Compare data-only versus default backend summaries.
        --feed-concurrency 16                Corpus preparation only, not a feed benchmark.
        --server-label TEXT                  Record actual image digest/version and hardware context.
        --out DIRECTORY                      Default: ignored artifacts/vector-search-benchmarks/...
        --self-test                          Check metrics and deterministic corpus without I/O.
        See docs/vector-search-benchmarks.md. No production throughput or optimal-HNSW claims.
        """);
    return;
}

var settings = Settings.Parse(args);
var corpus = settings.Dataset is { } path
    ? JsonSerializer.Deserialize<Corpus>(await File.ReadAllTextAsync(path), Corpus.Json)
        ?? throw new ArgumentException("Dataset JSON is null.")
    : Corpus.Synthetic(settings.Documents, settings.Dimensions, settings.Queries, settings.Seed);
corpus.Validate();
var runId = corpus.Fingerprint;
const string schema = "vector_benchmark";
const string documentNamespace = "vector_benchmark";
using var cancellation = new CancellationTokenSource(TimeSpan.FromHours(1));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var token = cancellation.Token;
using var client = new VespaClient(new VespaClientOptions
{
    Endpoint = settings.Endpoint,
    ConfigServerEndpoint = settings.ConfigEndpoint,
    Timeout = TimeSpan.FromMinutes(3),
    EnableRetry = false
});
var definition = new VectorStoreCollectionDefinition
{
    Properties =
    [
        new VectorStoreKeyProperty(nameof(BenchmarkRecord.Id), typeof(string)),
        new VectorStoreDataProperty(nameof(BenchmarkRecord.RunId), typeof(string)) { StorageName = "run_id", IsIndexed = true },
        new VectorStoreDataProperty(nameof(BenchmarkRecord.Bucket), typeof(int)) { StorageName = "bucket", IsIndexed = true },
        new VectorStoreDataProperty(nameof(BenchmarkRecord.Text), typeof(string)) { StorageName = "text" },
        new VectorStoreVectorProperty(nameof(BenchmarkRecord.Vector), typeof(ReadOnlyMemory<float>), corpus.Dimensions)
        {
            StorageName = "embedding", DistanceFunction = DistanceFunction.CosineSimilarity
        }
    ]
};
var schemaText = BenchmarkApplication.Schema(corpus.Dimensions);
if (Directory.Exists(settings.Output) && Directory.EnumerateFileSystemEntries(settings.Output).Any())
    throw new IOException("The output directory must be new or empty. Use a fresh --out path to avoid stale reports or overwriting files.");
Directory.CreateDirectory(settings.Output);
await File.WriteAllTextAsync(Path.Combine(settings.Output, ".gitignore"), "*\n", token);
await File.WriteAllTextAsync(Path.Combine(settings.Output, "schema.sd"), schemaText, token);
if (settings.Deploy)
{
    Console.WriteLine($"Replacing application at {settings.ConfigEndpoint} with the dedicated benchmark application.");
    using var config = new VespaClient(new VespaClientOptions { Endpoint = settings.ConfigEndpoint!, Timeout = TimeSpan.FromSeconds(5) });
    await WaitUntilAsync(() => config.IsReadyAsync(token), "config server", token);
    using var package = BenchmarkApplication.Package(schemaText);
    await client.Admin.DeployAsync(package, cancellationToken: token);
}
await WaitUntilAsync(() => client.IsReadyAsync(token), "query endpoint", token);
if (settings.Feed)
{
    Console.WriteLine($"Feeding {corpus.Documents.Length} documents; corpus {runId}.");
    var result = await client.Feed.BulkPutAsync(corpus.Documents.Select(d => new FeedDocument<Dictionary<string, object>>
    {
        Id = $"{runId}:{d.Id}",
        Fields = new()
        {
            ["run_id"] = runId,
            ["bucket"] = d.Bucket,
            ["text"] = d.Text,
            ["embedding"] = new Dictionary<string, object> { ["values"] = d.Vector }
        }
    }), schema, documentNamespace, settings.FeedConcurrency, token);
    if (!result.IsSuccess || result.HadIgnoredFields)
        throw new InvalidOperationException($"Corpus feed failed: {result.FailureCount} errors; ignored fields={result.HadIgnoredFields}. " +
            string.Join("; ", result.Errors.Take(3).Select(e => e.Message)));
    Console.WriteLine($"Corpus preparation: {result.SuccessCount} writes in {result.Duration.TotalSeconds:F2}s (not a feed benchmark).");
}

await WaitUntilAsync(async () =>
{
    var response = await client.Search.SearchAsync<Dictionary<string, JsonElement>>(new VespaSearchRequest
    {
        Yql = $"select documentid from sources {schema} where run_id contains \"{runId}\"",
        Hits = 0,
        ModelRestrict = schema
    }.WithTimeout(TimeSpan.FromSeconds(60)), token);
    if (response.Root.Errors is { Count: > 0 } errors)
        throw new InvalidOperationException(string.Join("; ", errors.Select(e => e.Message)));
    return response.Root.Coverage is { Full: true } && response.Root.Fields?.TotalCount == corpus.Documents.Length;
}, "complete corpus visibility (use --feed for a new dataset)", token);

var activeSelectivity = settings.Selectivity.Where(p => corpus.Documents.Any(d => d.Bucket < p)).ToArray();
if (activeSelectivity.Length == 0)
    throw new ArgumentException("Every selected filter matches zero documents; recall cannot be measured.");
foreach (var empty in settings.Selectivity.Except(activeSelectivity))
    Console.WriteLine($"Skipping bucket < {empty}: no eligible documents; recall is undefined.");
Dictionary<(int Selectivity, int Query), string[]> truth = [];
using (var store = CreateStore(false, 0, "vector_data"))
using (var collection = store.GetCollection<string, BenchmarkRecord>(schema, definition))
    foreach (var selectivity in activeSelectivity)
    {
        var expected = Math.Min(settings.K, corpus.Documents.Count(d => d.Bucket < selectivity));
        var searchOptions = Options(selectivity);
        for (var query = 0; query < corpus.Queries.Length; query++)
        {
            var ids = await SearchAsync(collection, corpus.Queries[query], searchOptions);
            if (ids.Length != expected)
                throw new InvalidOperationException($"Exact baseline returned {ids.Length} instead of {expected} hits. Check coverage, corpus, schema and timeout.");
            truth[(selectivity, query)] = ids;
        }
    }
Console.WriteLine("Exact ground truth ready. Measurements include client serialization/mapping and HTTP; warmup is excluded.");

List<Measurement> measurements = [];
var cases = (from selectivity in activeSelectivity
             from summary in settings.Summaries
             from exploration in new int?[] { null }.Concat(settings.Explore.Select(n => (int?)n))
             select new Scenario(selectivity, summary, exploration)).ToArray();
new Random(settings.Seed).Shuffle(cases);
foreach (var scenario in cases)
{
    using var store = CreateStore(scenario.Explore.HasValue, scenario.Explore ?? 0, scenario.Summary);
    using var collection = store.GetCollection<string, BenchmarkRecord>(schema, definition);
    var searchOptions = Options(scenario.Selectivity);
    for (var i = 0; i < settings.Warmup; i++)
        await SearchAsync(collection, corpus.Queries[i % corpus.Queries.Length], searchOptions);
    for (var repeat = 0; repeat < settings.Repeats; repeat++)
        for (var query = 0; query < corpus.Queries.Length; query++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var start = Stopwatch.GetTimestamp();
            var ids = await SearchAsync(collection, corpus.Queries[query], searchOptions);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            measurements.Add(new(scenario.Mode, scenario.Explore, scenario.Summary, scenario.Selectivity, query, repeat,
                elapsed, allocated, Metrics.Recall(truth[(scenario.Selectivity, query)], ids), ids.Length));
        }
    Console.WriteLine($"{scenario.Mode}, explore={scenario.Explore?.ToString() ?? "n/a"}, summary={scenario.Summary}, bucket<{scenario.Selectivity}: complete.");
}

var aggregates = measurements.GroupBy(m => new { m.Mode, m.Explore, m.Summary, m.Selectivity }).Select(group => new
{
    group.Key.Mode,
    group.Key.Explore,
    group.Key.Summary,
    group.Key.Selectivity,
    EligibleDocuments = corpus.Documents.Count(d => d.Bucket < group.Key.Selectivity),
    ActualSelectivity = (double)corpus.Documents.Count(d => d.Bucket < group.Key.Selectivity) / corpus.Documents.Length,
    Samples = group.Count(),
    RecallAtK = group.Average(m => m.RecallAtK),
    P50Milliseconds = Metrics.Percentile(group.Select(m => m.Milliseconds), 0.5),
    P95Milliseconds = Metrics.Percentile(group.Select(m => m.Milliseconds), 0.95),
    MeanProcessAllocatedBytes = group.Average(m => m.ProcessAllocatedBytes),
    MeanReturnedHits = group.Average(m => m.Hits)
}).OrderBy(g => g.Selectivity).ThenBy(g => g.Summary).ThenBy(g => g.Mode).ThenBy(g => g.Explore).ToArray();
var report = new
{
    CompletedAtUtc = DateTimeOffset.UtcNow,
    Settings = settings,
    DatasetKind = settings.Dataset is null ? "synthetic clustered Gaussian; not semantic relevance evidence" : "external precomputed embeddings",
    DatasetSha256 = runId,
    Documents = corpus.Documents.Length,
    Dimensions = corpus.Dimensions,
    Queries = corpus.Queries.Length,
    Runtime = RuntimeInformation.FrameworkDescription,
    OS = RuntimeInformation.OSDescription,
    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    ClientProcessorCount = Environment.ProcessorCount,
    ConnectorAssemblyVersion = typeof(VespaVectorStore).Assembly.GetName().Version?.ToString(),
    Notes = new[] { "Single client, serial warmed queries; no QPS/load/concurrency claim.",
        "Allocation deltas cover the whole client process, including background work; not server memory or transport bytes.",
        "Strict ID overlap against exact top-k; equal-score boundary ties may lower overlap without worse distances.",
        "Both summary cases use the same explicit field projection. Modern Vespa partial-summary handling may fetch the same backend fields; no byte-reduction or named-summary performance advantage is assumed.",
        "ANN with selective filters may fall back to exact; approximate=true alone does not prove HNSW execution.",
        "All measured queries must succeed with complete coverage. Failed runs do not produce a completed report." },
    Aggregates = aggregates,
    Measurements = measurements
};
await File.WriteAllTextAsync(Path.Combine(settings.Output, "results.json"), JsonSerializer.Serialize(report, Corpus.Json), token);
var csv = new StringBuilder("mode,explore,summary,selectivity,query,repeat,milliseconds,process_allocated_bytes,recall_at_k,hits\n");
foreach (var m in measurements)
    csv.AppendLine(FormattableString.Invariant($"{m.Mode},{m.Explore},{m.Summary},{m.Selectivity},{m.Query},{m.Repeat},{m.Milliseconds:R},{m.ProcessAllocatedBytes},{m.RecallAtK:R},{m.Hits}"));
await File.WriteAllTextAsync(Path.Combine(settings.Output, "measurements.csv"), csv.ToString(), token);
var markdown = new StringBuilder($"# Vespa ANN benchmark\n\nDataset SHA-256: `{runId}`. Documents: {corpus.Documents.Length}; dimensions: {corpus.Dimensions}; queries: {corpus.Queries.Length}; k: {settings.K}.\n\n");
markdown.AppendLine(settings.Dataset is null ? "Synthetic clustered Gaussian vectors; these results do not establish semantic retrieval quality.\n" : "External precomputed embeddings; recall measures agreement with exact vector search, not answer relevance.\n");
markdown.AppendLine("| Mode | Extra hits | Summary | Eligible | Recall@k | p50 ms | p95 ms | Process bytes/query | Samples |\n|---|---:|---|---:|---:|---:|---:|---:|---:|");
foreach (var g in aggregates)
    markdown.AppendLine(FormattableString.Invariant($"| {g.Mode} | {g.Explore?.ToString() ?? "—"} | {g.Summary} | {g.EligibleDocuments} | {g.RecallAtK:F4} | {g.P50Milliseconds:F3} | {g.P95Milliseconds:F3} | {g.MeanProcessAllocatedBytes:F0} | {g.Samples} |"));
markdown.AppendLine("\n" + string.Join("\n\n", report.Notes));
await File.WriteAllTextAsync(Path.Combine(settings.Output, "README.md"), markdown.ToString(), token);
Console.WriteLine($"Results: {Path.GetFullPath(settings.Output)}");

VespaVectorStore CreateStore(bool approximate, int explore, string summary) => new(client, new()
{
    Namespace = documentNamespace,
    RankProfile = "vector_search",
    SummaryClass = summary,
    Approximate = approximate,
    HnswExploreAdditionalHits = approximate ? explore : null,
    SearchTimeout = TimeSpan.FromSeconds(60)
});

VectorSearchOptions<BenchmarkRecord> Options(int selectivity)
{
    Expression<Func<BenchmarkRecord, bool>> filter = record => record.RunId == runId && record.Bucket < selectivity;
    return new() { Filter = filter, IncludeVectors = false };
}

async Task<string[]> SearchAsync(VectorStoreCollection<string, BenchmarkRecord> collection, float[] vector, VectorSearchOptions<BenchmarkRecord> options)
{
    List<string> result = [];
    await foreach (var hit in collection.SearchAsync(vector, settings.K, options, token))
        result.Add(hit.Record.Id);
    return result.ToArray();
}

static async Task WaitUntilAsync(Func<Task<bool>> probe, string description, CancellationToken token)
{
    var start = Stopwatch.GetTimestamp();
    while (!await probe())
    {
        if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromMinutes(5))
            throw new TimeoutException($"Timed out waiting for {description}.");
        await Task.Delay(500, token);
    }
}

internal sealed class BenchmarkRecord
{
    public string Id { get; set; } = "";
    public string RunId { get; set; } = "";
    public int Bucket { get; set; }
    public string Text { get; set; } = "";
    public ReadOnlyMemory<float> Vector { get; set; }
}

internal sealed record Scenario(int Selectivity, string Summary, int? Explore)
{
    internal string Mode => Explore.HasValue ? "approximate" : "exact";
}
internal sealed record Measurement(string Mode, int? Explore, string Summary, int Selectivity, int Query, int Repeat,
    double Milliseconds, long ProcessAllocatedBytes, double RecallAtK, int Hits);
