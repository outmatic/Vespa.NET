using System.Diagnostics;
using System.Text.Json;
using Vespa.AgentFrameworkSample;
using Vespa.Models;

namespace Vespa.RetrievalEvaluation;

internal static class LatencyEvaluation
{
    internal static async Task RunAsync(IVespaClient client, EvaluationCorpus corpus, string corpusHash, string datasetHash,
        RetrievalSettings[] configurations, int k, int repeats, int warmup, bool tracesOnly, string output,
        string? serverLabel, string schemaText, string configurationHash, JsonElement configurationSource, CancellationToken ct)
    {
        var documentIds = corpus.Documents.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var lengthOrder = corpus.Queries.OrderBy(q => q.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length)
            .ThenBy(q => q.Id, StringComparer.Ordinal).ToArray();
        // Fixed length quantiles, selected without relevance or timing observations.
        var diagnosticQueries = new[] { lengthOrder[lengthOrder.Length / 10], lengthOrder[lengthOrder.Length / 2],
            lengthOrder[lengthOrder.Length * 9 / 10] }.DistinctBy(q => q.Id).ToArray();
        List<object> traces = [];
        foreach (var query in diagnosticQueries)
            foreach (var configuration in configurations)
            {
                var (_, _, trace) = await SearchAsync(configuration, query, profile: true);
                traces.Add(new { queryId = query.Id, mode = configuration.Name,
                    words = query.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, trace });
            }
        await WriteAsync("latency-traces.json", new { datasetSha256 = datasetHash, corpusSha256 = corpusHash,
            schemaSha256 = SchemaHash(), configurationSha256 = configurationHash, configurationSource, configurations, k,
            serverLabel, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, traces });
        if (tracesOnly)
            return;

        foreach (var configuration in configurations)
            for (var i = 0; i < warmup; i++)
                await SearchAsync(configuration, corpus.Queries[i % corpus.Queries.Length]);
        List<object> results = [];
        var random = new Random(1729);
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            var queries = corpus.Queries.ToArray();
            random.Shuffle(queries);
            for (var i = 0; i < queries.Length; i++)
            {
                var order = configurations.ToArray();
                random.Shuffle(order);
                foreach (var configuration in order)
                {
                    var (hits, milliseconds, _) = await SearchAsync(configuration, queries[i]);
                    results.Add(new { queryId = queries[i].Id, mode = configuration.Name, repeat, milliseconds, hits });
                }
                if ((i + 1) % 100 == 0 || i + 1 == queries.Length)
                    Console.WriteLine($"Latency repeat {repeat + 1}/{repeats}: {i + 1}/{queries.Length} queries, every configuration.");
            }
        }
        await WriteAsync("latency-results.json", new
        {
            datasetSha256 = datasetHash, corpusSha256 = corpusHash, schemaSha256 = SchemaHash(),
            configurationSha256 = configurationHash, configurationSource, configurations, k, repeats, warmup,
            serverLabel, completedAtUtc = DateTimeOffset.UtcNow, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            note = "Warmed serial client latency, including native request/response processing and summaries, excluding embeddings. Query and configuration order shuffled seed 1729; traces excluded.",
            results
        });

        string SchemaHash() => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(schemaText)));

        async Task WriteAsync(string name, object value) => await File.WriteAllTextAsync(Path.Combine(output, name),
            JsonSerializer.Serialize(value, EvaluationCorpus.Json), ct);

        async Task<(List<Hit> Hits, double Milliseconds, object? Trace)> SearchAsync(RetrievalSettings configuration,
            EvaluationQuery query, bool profile = false)
        {
            var start = Stopwatch.GetTimestamp();
            var request = NativeRetrieval.CreateRequest(query.Text, query.Vector, "retrieval_evaluation",
                EvaluationScope.Filter(corpusHash, 100, query), configuration, k);
            if (profile)
            {
                request.TraceLevel = 3;
                request.TraceExplainLevel = 2;
                request.TraceProfileDepth = 8;
                request.TraceQuery = false;
            }
            var response = await client.Search.SearchAsync<Dictionary<string, JsonElement>>(request, ct);
            if (response.Root.Errors is { Count: > 0 } errors)
                throw new InvalidOperationException(string.Join("; ", errors.Select(e => e.Message)));
            if (response.Root.Coverage is not { Full: true })
                throw new InvalidOperationException("Incomplete coverage in latency evaluation.");
            if (profile && response.Trace is null)
                throw new InvalidOperationException("The profiling request returned no trace.");
            List<Hit> hits = [];
            foreach (var hit in response.Root.Children)
            {
                if (!VespaDocumentId.TryParse(hit.FullId, out var id) || id.Namespace != "retrieval_evaluation" ||
                    id.DocumentType != "retrieval_evaluation" || !documentIds.Contains(id.UserSpecified) ||
                    id.UserSpecified == query.ExcludeDocumentId || !double.IsFinite(hit.Relevance))
                    throw new InvalidOperationException("A latency result violated document identity or exclusion.");
                hits.Add(new(id.UserSpecified, hit.Relevance));
            }
            if (hits.Count > k || hits.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count() != hits.Count)
                throw new InvalidOperationException("Duplicate or excess latency results.");
            if (configuration.Mode == "semantic" && !configuration.Approximate && hits.Count != Math.Min(k, documentIds.Count - (query.ExcludeDocumentId is null ? 0 : 1)))
                throw new InvalidOperationException("Incomplete exact semantic result set.");
            return (hits, Stopwatch.GetElapsedTime(start).TotalMilliseconds, response.Trace);
        }
    }

    private sealed record Hit(string Id, double Score);
}
