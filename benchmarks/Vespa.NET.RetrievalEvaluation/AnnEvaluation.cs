using System.Diagnostics;
using System.Text.Json;
using Vespa.AgentFrameworkSample;
using Vespa.Models;

namespace Vespa.RetrievalEvaluation;

internal static class AnnEvaluation
{
    internal static async Task RunAsync(IVespaClient client, EvaluationCorpus corpus, string corpusHash, string datasetHash,
        RetrievalSettings[] configurations, int k, int[] scopes, int[] explorations, int repeats, int warmup, bool tracesOnly,
        string output, string? serverLabel, string schemaText, CancellationToken ct)
    {
        var selected = configurations.Where(c => c.Mode is "semantic" or "hybrid_normalized").ToArray();
        if (selected.Length != 2 || selected.Select(c => c.Mode).Distinct(StringComparer.Ordinal).Count() != 2)
            throw new ArgumentException("ANN evaluation needs exactly one semantic and one normalized-hybrid configuration.");
        var documents = corpus.Documents.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var buckets = documents.Keys.ToDictionary(id => id, EvaluationScope.Bucket, StringComparer.Ordinal);
        var eligibleCounts = scopes.ToDictionary(scope => scope, scope => buckets.Values.Count(bucket => bucket < scope));
        List<object> baselines = [];
        foreach (var configuration in tracesOnly ? [] : selected)
            foreach (var scope in scopes)
                foreach (var query in corpus.Queries)
                {
                    var (hits, _, _) = await SearchAsync(configuration, query, scope, false, null);
                    if (configuration.Mode == "semantic")
                    {
                        var eligible = eligibleCounts[scope] - (query.ExcludeDocumentId is { } excluded && buckets[excluded] < scope ? 1 : 0);
                        if (hits.Count != Math.Min(k, eligible))
                            throw new InvalidOperationException("The exact baseline returned fewer hits than the eligible corpus requires.");
                    }
                    baselines.Add(new { mode = configuration.Name, scopePercent = scope, queryId = query.Id, hits });
                }
        if (!tracesOnly)
            Console.WriteLine("Exact baselines ready for all query/filter pairs.");
        var cases = (from configuration in selected
                     from scope in scopes
                     from explore in new int?[] { null }.Concat(explorations.Select(e => (int?)e))
                     select new Scenario(configuration, scope, explore)).ToArray();
        new Random(1729).Shuffle(cases);
        List<object> results = [];
        List<object> traces = [];
        foreach (var scenario in cases)
        {
            var approximate = scenario.Explore is not null;
            for (var i = 0; i < (tracesOnly ? 0 : warmup); i++)
                await SearchAsync(scenario.Configuration, corpus.Queries[i % corpus.Queries.Length], scenario.Scope, approximate, scenario.Explore);
            for (var repeat = 0; repeat < (tracesOnly ? 0 : repeats); repeat++)
                foreach (var query in corpus.Queries)
                {
                    var (hits, milliseconds, _) = await SearchAsync(scenario.Configuration, query, scenario.Scope, approximate, scenario.Explore);
                    results.Add(new
                    {
                        mode = scenario.Configuration.Name,
                        scopePercent = scenario.Scope,
                        approximate,
                        explore = scenario.Explore,
                        repeat,
                        queryId = query.Id,
                        milliseconds,
                        hits
                    });
                }
            // Profiling requests are separate from latency measurements and excluded from them.
            if (approximate)
            {
                var (_, _, trace) = await SearchAsync(scenario.Configuration, corpus.Queries[0], scenario.Scope,
                    true, scenario.Explore, trace: true);
                traces.Add(new { mode = scenario.Configuration.Name, scopePercent = scenario.Scope, explore = scenario.Explore, trace });
            }
            Console.WriteLine($"ANN: {scenario.Configuration.Name}, scope={scenario.Scope}, explore={scenario.Explore?.ToString() ?? "exact"}: complete.");
        }
        await File.WriteAllTextAsync(Path.Combine(output, tracesOnly ? "ann-traces.json" : "ann-results.json"), JsonSerializer.Serialize(new
        {
            datasetSha256 = datasetHash,
            corpusSha256 = corpusHash,
            completedAtUtc = DateTimeOffset.UtcNow,
            k,
            scopes,
            explorations,
            repeats,
            warmup,
            configurations = selected,
            baselines,
            results,
            traces,
            bucketDefinition = "first four SHA-256(id) bytes as unsigned big-endian integer, modulo 100",
            serverLabel,
            schemaSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(schemaText))),
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            note = "approximate=true requests ANN; selective filters can cause Vespa to use exact search. Inspect recorded query traces."
        }, EvaluationCorpus.Json), ct);

        async Task<(List<Hit> Hits, double Milliseconds, object? Trace)> SearchAsync(RetrievalSettings configuration,
            EvaluationQuery query, int scope, bool approximate, int? exploration, bool trace = false)
        {
            var settings = configuration with { HnswExploreAdditionalHits = exploration };
            var request = NativeRetrieval.CreateRequest(query.Text, query.Vector, "retrieval_evaluation",
                EvaluationScope.Filter(corpusHash, scope, query), settings, k, approximate);
            if (trace)
                request = request with
                {
                    TraceLevel = 3,
                    TraceExplainLevel = 2,
                    TraceProfileDepth = 4,
                    TraceQuery = false
                };
            var start = Stopwatch.GetTimestamp();
            var response = await client.Search.SearchAsync<Dictionary<string, JsonElement>>(request, ct);
            if (response.Root.Errors is { Count: > 0 } errors)
                throw new InvalidOperationException(string.Join("; ", errors.Select(e => e.Message)));
            if (response.Root.Coverage is not { Full: true })
                throw new InvalidOperationException("Incomplete search coverage during ANN evaluation.");
            List<Hit> hits = [];
            foreach (var hit in response.Root.Children)
            {
                if (!VespaDocumentId.TryParse(hit.FullId, out var id) || id.Namespace != "retrieval_evaluation" ||
                    id.DocumentType != "retrieval_evaluation" || !documents.ContainsKey(id.UserSpecified) ||
                    buckets[id.UserSpecified] >= scope || id.UserSpecified == query.ExcludeDocumentId || !double.IsFinite(hit.Relevance))
                    throw new InvalidOperationException("A result violated document identity or scope constraints.");
                hits.Add(new(id.UserSpecified, hit.Relevance));
            }
            if (hits.Count > k || hits.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count() != hits.Count)
                throw new InvalidOperationException("Duplicate or excess ANN result IDs.");
            return (hits, Stopwatch.GetElapsedTime(start).TotalMilliseconds, response.Trace);
        }
    }

    private sealed record Scenario(RetrievalSettings Configuration, int Scope, int? Explore);
    private sealed record Hit(string Id, double Score);
}
