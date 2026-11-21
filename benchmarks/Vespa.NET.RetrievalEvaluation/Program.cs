using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.VectorData;
using Vespa;
using Vespa.Feed;
using Vespa.Models;
using Vespa.RetrievalEvaluation;
using Vespa.VectorData;
using Vespa.AgentFrameworkSample;

if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine("""
        Opt-in retrieval evaluation on a dedicated Vespa instance (no LLM judge).
        --dataset FILE --endpoint URL --out DIRECTORY  Required. Dataset includes real vectors and qrels.
        --deploy --config-endpoint URL                Replace the WHOLE dedicated application.
        --feed                                        Feed the complete corpus; otherwise reuse it.
        --k 10 --candidates 100                        Same lexical/dense candidate target; exact vectors.
        --server-label TEXT                           Image digest/hardware for the report.
        --native --config FILE                        Native multi-field retrieval using named configurations.
        --ann --scopes 100,10,1 --explore 0,32,128     HNSW/filter matrix; requires --native.
        --repeats 3 --warmup 5                        ANN latency samples per query and excluded warmups.
        --trace-only                                 With --ann, --latency or --load, capture plans only.
        --latency --repeats 3 --warmup 5              Interleaved native configurations with quality and p95.
        --load --scopes 100,10,1 --concurrency 1,4,8  Filtered closed-loop load; requires --native.
        --timeout-minutes 30                         Whole-run limit, including deployment and feeding.
        Writes complete rankings to results.json for scripts/retrieval-quality.py report.
        Nothing deploys, feeds, downloads or deletes by default. See docs/retrieval-quality.md.
        """);
    return;
}

var switches = new HashSet<string>(["--deploy", "--feed", "--native", "--ann", "--latency", "--load", "--trace-only"], StringComparer.Ordinal);
var values = new Dictionary<string, string>(StringComparer.Ordinal);
var flags = new HashSet<string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length; i++)
    if (switches.Contains(args[i]))
    {
        if (!flags.Add(args[i]))
            throw new ArgumentException("Duplicate flag: " + args[i]);
    }
    else if (args[i] is "--dataset" or "--endpoint" or "--out" or "--config-endpoint" or "--k" or "--candidates" or "--server-label" or "--config" or "--scopes" or "--explore" or "--repeats" or "--warmup" or "--timeout-minutes" or "--concurrency")
    {
        var key = args[i];
        if (++i >= args.Length || !values.TryAdd(key, args[i]))
            throw new ArgumentException("Missing or duplicate value for " + key);
    }
    else
        throw new ArgumentException("Unknown option: " + args[i]);

string Required(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
    ? value : throw new ArgumentException("Missing " + key);
var datasetPath = Required("--dataset");
var endpoint = Required("--endpoint");
var output = Required("--out");
var k = int.Parse(values.GetValueOrDefault("--k", "10"), CultureInfo.InvariantCulture);
var candidates = int.Parse(values.GetValueOrDefault("--candidates", "100"), CultureInfo.InvariantCulture);
if (k < 1 || k > 100 || candidates < k || candidates > 1000)
    throw new ArgumentException("Require 1 <= k <= 100 and k <= candidates <= 1000.");
if (flags.Contains("--deploy"))
    _ = Required("--config-endpoint");
else if (values.ContainsKey("--config-endpoint"))
    throw new ArgumentException("--config-endpoint requires --deploy.");
if (!flags.Contains("--native") && values.ContainsKey("--config"))
    throw new ArgumentException("--config requires --native.");
if (flags.Contains("--native") && values.ContainsKey("--candidates"))
    throw new ArgumentException("Native candidate budgets belong to each configuration in --config.");
var scopes = values.GetValueOrDefault("--scopes", "100,10,1").Split(',').Select(int.Parse).ToArray();
var explorations = values.GetValueOrDefault("--explore", "0,32,128").Split(',').Select(int.Parse).ToArray();
var concurrencies = values.GetValueOrDefault("--concurrency", "1,4,8").Split(',').Select(int.Parse).ToArray();
var repeats = int.Parse(values.GetValueOrDefault("--repeats", "3"), CultureInfo.InvariantCulture);
var warmup = int.Parse(values.GetValueOrDefault("--warmup", "5"), CultureInfo.InvariantCulture);
var timeoutMinutes = int.Parse(values.GetValueOrDefault("--timeout-minutes", "30"), CultureInfo.InvariantCulture);
if (timeoutMinutes is < 1 or > 240)
    throw new ArgumentException("Require 1 <= timeout-minutes <= 240.");
if (flags.Contains("--ann") && !flags.Contains("--native"))
    throw new ArgumentException("--ann requires --native.");
if (flags.Contains("--latency") && (!flags.Contains("--native") || flags.Contains("--ann")))
    throw new ArgumentException("--latency requires --native and cannot combine with --ann.");
if (flags.Contains("--load") && (!flags.Contains("--native") || flags.Contains("--ann") || flags.Contains("--latency")))
    throw new ArgumentException("--load requires --native and cannot combine with --ann or --latency.");
var measurement = flags.Contains("--ann") || flags.Contains("--latency") || flags.Contains("--load");
if (flags.Contains("--trace-only") && !measurement)
    throw new ArgumentException("--trace-only requires --ann, --latency or --load.");
if ((!flags.Contains("--ann") && values.ContainsKey("--explore")) ||
    (!flags.Contains("--load") && values.ContainsKey("--concurrency")) ||
    (!flags.Contains("--ann") && !flags.Contains("--load") && values.ContainsKey("--scopes")) ||
    (!measurement && new[] { "--repeats", "--warmup" }.Any(values.ContainsKey)))
    throw new ArgumentException("Exploration requires --ann, concurrency requires --load, scopes require --ann/--load, timing requires a measurement mode.");
if (scopes.Length == 0 || scopes.Any(s => s is < 1 or > 100) || scopes.Distinct().Count() != scopes.Length ||
    explorations.Length == 0 || explorations.Any(e => e < 0) || explorations.Distinct().Count() != explorations.Length || repeats < 1 || warmup < 0)
    throw new ArgumentException("Invalid ANN scope, exploration, repeat or warmup settings.");
if (!concurrencies.Contains(1) || concurrencies.Any(c => c is < 1 or > 64) || concurrencies.Distinct().Count() != concurrencies.Length)
    throw new ArgumentException("Concurrency levels must be distinct, in [1,64], and include the serial control 1.");
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
    throw new IOException("Use a new or empty output directory; completed results must not be overwritten.");

var bytes = await File.ReadAllBytesAsync(datasetPath);
var corpus = JsonSerializer.Deserialize<EvaluationCorpus>(bytes, EvaluationCorpus.Json)
    ?? throw new ArgumentException("Dataset is null.");
var dimensions = corpus.Validate();
if (!flags.Contains("--native") && corpus.Queries.Any(q => q.ExcludeDocumentId is not null))
    throw new ArgumentException("Identical-ID exclusion requires the native evaluation backend.");
var datasetSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
var runId = flags.Contains("--native")
    ? Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(corpus.Documents, EvaluationCorpus.Json)))
    : datasetSha256;
RetrievalSettings[] configurations = [];
string? configurationSha256 = null;
JsonElement? configurationSource = null;
if (flags.Contains("--native"))
{
    var configurationBytes = await File.ReadAllBytesAsync(Required("--config"));
    configurationSha256 = Convert.ToHexStringLower(SHA256.HashData(configurationBytes));
    using var configurationFile = JsonDocument.Parse(configurationBytes);
    var root = configurationFile.RootElement;
    configurationSource = root.Clone();
    configurations = (root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("configurations"))
        .Deserialize<RetrievalSettings[]>(EvaluationCorpus.Json) ?? throw new ArgumentException("Missing configurations.");
}
if (flags.Contains("--native") && (configurations.Length == 0 || configurations.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != configurations.Length))
    throw new ArgumentException("Native configurations need distinct names.");
foreach (var configuration in configurations)
    configuration.Validate(k);
if (flags.Contains("--load") && configurations.Any(c => c.Mode != "hybrid_normalized" || !c.Approximate))
    throw new ArgumentException("This load study accepts explicit normalized-hybrid ANN presets only.");
if (flags.Contains("--ann") && (configurations.Count(c => c.Mode == "semantic") != 1 ||
    configurations.Count(c => c.Mode == "hybrid_normalized") != 1))
    throw new ArgumentException("ANN evaluation needs exactly one semantic and one normalized-hybrid configuration.");
if (flags.Contains("--native") && corpus.Documents.Any(d => d.Title is null || d.Body is null))
    throw new ArgumentException("Native multi-field evaluation needs separate title and body fields.");
const string schema = "retrieval_evaluation";
const string documentNamespace = "retrieval_evaluation";
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var ct = cancellation.Token;
using var client = new VespaClient(new VespaClientOptions
{
    Endpoint = endpoint,
    ConfigServerEndpoint = values.GetValueOrDefault("--config-endpoint"),
    Timeout = TimeSpan.FromMinutes(2),
    EnableRetry = false
});
var schemaText = flags.Contains("--native") ? EvaluationApplication.NativeSchema(dimensions) : EvaluationApplication.Schema(dimensions, candidates * 2);
Directory.CreateDirectory(output);
await File.WriteAllTextAsync(Path.Combine(output, ".gitignore"), "*\n", ct);
await File.WriteAllTextAsync(Path.Combine(output, "schema.sd"), schemaText, ct);
if (flags.Contains("--deploy"))
{
    Console.WriteLine($"Replacing the WHOLE dedicated application at {values["--config-endpoint"]}.");
    using var config = new VespaClient(new VespaClientOptions { Endpoint = values["--config-endpoint"], Timeout = TimeSpan.FromSeconds(5) });
    await WaitUntilAsync(() => config.IsReadyAsync(ct), "config server");
    using var package = EvaluationApplication.Package(schemaText);
    await client.Admin.DeployAsync(package, cancellationToken: ct);
}
await WaitUntilAsync(() => client.IsReadyAsync(ct), "query service");
if (flags.Contains("--feed"))
{
    var fed = await client.Feed.BulkPutAsync(corpus.Documents.Select(d => new FeedDocument<Dictionary<string, object>>
    {
        Id = d.Id,
        Fields = DocumentFields(d)
    }), schema, documentNamespace, 8, ct);
    if (!fed.IsSuccess || fed.HadIgnoredFields)
        throw new InvalidOperationException("Corpus feed was not fully acknowledged: " + string.Join("; ", fed.Errors.Take(3).Select(e => e.Message)));
}
await WaitUntilAsync(async () =>
{
    var response = await client.Search.SearchAsync<Dictionary<string, JsonElement>>(new VespaSearchRequest
    {
        Yql = $"select documentid from sources {schema} where run_id contains \"{runId}\"",
        Hits = 0
    }, ct);
    CheckResponse(response);
    return response.Root.Fields?.TotalCount == corpus.Documents.Length;
}, "complete corpus visibility");

if (flags.Contains("--ann"))
{
    await AnnEvaluation.RunAsync(client, corpus, runId, datasetSha256, configurations, k, scopes, explorations,
        repeats, warmup, flags.Contains("--trace-only"), output, values.GetValueOrDefault("--server-label"), schemaText, ct);
    Console.WriteLine("Completed ANN evaluation: " + Path.GetFullPath(output));
    return;
}
if (flags.Contains("--latency"))
{
    await LatencyEvaluation.RunAsync(client, corpus, runId, datasetSha256, configurations, k, repeats, warmup,
        flags.Contains("--trace-only"), output, values.GetValueOrDefault("--server-label"), schemaText,
        configurationSha256!, configurationSource!.Value, ct);
    Console.WriteLine("Completed latency evaluation: " + Path.GetFullPath(output));
    return;
}
if (flags.Contains("--load"))
{
    await LoadEvaluation.RunAsync(client, corpus, runId, datasetSha256, configurations, k, scopes, concurrencies,
        repeats, warmup, flags.Contains("--trace-only"), output, values.GetValueOrDefault("--server-label"),
        schemaText, configurationSha256!, configurationSource!.Value, ct);
    Console.WriteLine("Completed load evaluation: " + Path.GetFullPath(output));
    return;
}

var definition = new VectorStoreCollectionDefinition
{
    Properties =
    [
        new VectorStoreKeyProperty(nameof(EvaluationRecord.Id), typeof(string)),
        new VectorStoreDataProperty(nameof(EvaluationRecord.RunId), typeof(string)) { StorageName = "run_id", IsIndexed = true },
        new VectorStoreDataProperty(nameof(EvaluationRecord.Text), typeof(string)) { StorageName = "text", IsFullTextIndexed = true },
        new VectorStoreVectorProperty(nameof(EvaluationRecord.Vector), typeof(ReadOnlyMemory<float>), dimensions)
        {
            StorageName = "embedding", DistanceFunction = DistanceFunction.CosineSimilarity
        }
    ]
};
using var vectorStore = flags.Contains("--native") ? null : CreateStore("hybrid_normalized");
using var rrfStore = flags.Contains("--native") ? null : CreateStore("hybrid_rrf");
using var collection = vectorStore?.GetCollection<string, EvaluationRecord>(schema, definition);
using var rrf = rrfStore?.GetCollection<string, EvaluationRecord>(schema, definition);
List<object> results = [];
var modes = flags.Contains("--native") ? configurations.Select(c => c.Name).ToArray()
    : ["lexical", "semantic", "hybrid_normalized", "hybrid_rrf"];
var random = new Random(1729);
foreach (var query in corpus.Queries)
{
    var order = modes.ToArray();
    random.Shuffle(order);
    foreach (var mode in order)
    {
        var start = Stopwatch.GetTimestamp();
        List<ResultHit> hits = [];
        if (flags.Contains("--native"))
        {
            var configuration = configurations.Single(c => c.Name == mode);
            var filter = EvaluationScope.Filter(runId, 100, query);
            var request = NativeRetrieval.CreateRequest(query.Text, query.Vector, schema, filter, configuration, k, approximate: false);
            var response = await client.Search.SearchAsync<Dictionary<string, JsonElement>>(request, ct);
            CheckResponse(response);
            foreach (var hit in response.Root.Children)
            {
                if (!VespaDocumentId.TryParse(hit.FullId, out var id) || id.Namespace != documentNamespace || id.DocumentType != schema)
                    throw new InvalidOperationException("Unexpected result identity.");
                hits.Add(new(id.UserSpecified, hit.Relevance));
            }
        }
        else if (mode == "lexical")
        {
            var terms = string.Join(", ", query.Keywords.Select(term => "text contains " + JsonSerializer.Serialize(term)));
            var response = await client.Search.SearchAsync<Dictionary<string, JsonElement>>(new VespaSearchRequest
            {
                Yql = $"select documentid from sources {schema} where (run_id contains \"{runId}\") and ({{totalTargetHits:{candidates}}}weakAnd({terms}))",
                Hits = k,
                Ranking = new() { Profile = "lexical" }
            }.WithTimeout(TimeSpan.FromSeconds(60)), ct);
            CheckResponse(response);
            foreach (var hit in response.Root.Children)
            {
                if (!VespaDocumentId.TryParse(hit.FullId, out var id) || id.Namespace != documentNamespace || id.DocumentType != schema)
                    throw new InvalidOperationException("Unexpected result identity.");
                hits.Add(new(id.UserSpecified, hit.Relevance));
            }
        }
        else
        {
            var selected = (mode == "hybrid_rrf" ? rrf : collection)!;
            var search = mode == "semantic"
                ? selected.SearchAsync(query.Vector, k, new() { Filter = d => d.RunId == runId }, ct)
                : ((IKeywordHybridSearchable<EvaluationRecord>)selected).HybridSearchAsync(query.Vector, query.Keywords, k,
                    new() { Filter = d => d.RunId == runId }, ct);
            await foreach (var hit in search)
                hits.Add(new(hit.Record.Id, hit.Score ?? throw new InvalidOperationException("Missing score.")));
        }
        results.Add(new { queryId = query.Id, mode, milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds, hits });
    }
    var completed = results.Count / modes.Length;
    if (completed % 25 == 0 || completed == corpus.Queries.Length)
        Console.WriteLine($"{completed}/{corpus.Queries.Length}: {query.Id}");
}
// Publish the completed run only after every query/configuration succeeded.
await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
{
    datasetSha256,
    corpusSha256 = runId,
    schemaSha256 = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(schemaText))),
    backend = flags.Contains("--native") ? "native" : "vectordata",
    configurations,
    configurationSha256,
    configurationSource,
    completedAtUtc = DateTimeOffset.UtcNow,
    k,
    candidates,
    modes,
    approximate = false,
    endpoint,
    serverLabel = values.GetValueOrDefault("--server-label"),
    runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    metadata = corpus.Metadata,
    results
}, EvaluationCorpus.Json), ct);
Console.WriteLine("Completed rankings: " + Path.GetFullPath(Path.Combine(output, "results.json")));

Dictionary<string, object> DocumentFields(EvaluationDocument document)
{
    Dictionary<string, object> fields = new()
    {
        ["run_id"] = runId,
        ["text"] = document.Text,
        ["embedding"] = new Dictionary<string, object> { ["values"] = document.Vector }
    };
    if (flags.Contains("--native"))
    {
        fields["title"] = document.Title!;
        fields["body"] = document.Body!;
        fields["record_id"] = document.Id;
        fields["bucket"] = EvaluationScope.Bucket(document.Id);
    }
    return fields;
}

VespaVectorStore CreateStore(string hybridProfile) => new(client, new()
{
    Namespace = documentNamespace,
    RankProfile = "semantic",
    HybridRankProfile = hybridProfile,
    HybridVectorTargetHits = candidates,
    HybridKeywordTargetHits = candidates,
    Approximate = false,
    SummaryClass = "evaluation_data",
    SearchTimeout = TimeSpan.FromSeconds(60)
});

async Task WaitUntilAsync(Func<Task<bool>> ready, string description)
{
    var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
    while (!await ready())
    {
        if (DateTimeOffset.UtcNow >= deadline)
            throw new TimeoutException("Waiting for " + description);
        await Task.Delay(500, ct);
    }
}

static void CheckResponse(VespaSearchResponse<Dictionary<string, JsonElement>> response)
{
    if (response.Root.Errors is { Count: > 0 } errors)
        throw new InvalidOperationException(string.Join("; ", errors.Select(e => e.Message)));
    if (response.Root.Coverage is not { Full: true })
        throw new InvalidOperationException("Vespa did not report complete search coverage.");
}

file sealed record ResultHit(string Id, double Score);
