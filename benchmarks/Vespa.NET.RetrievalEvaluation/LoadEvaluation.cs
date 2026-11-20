using System.Diagnostics;
using System.Text.Json;
using Vespa.AgentFrameworkSample;
using Vespa.Models;

namespace Vespa.RetrievalEvaluation;

internal static class LoadEvaluation
{
    internal static async Task RunAsync(IVespaClient client, EvaluationCorpus corpus, string corpusHash, string datasetHash,
        RetrievalSettings[] configurations, int k, int[] scopes, int[] concurrencies, int repeats, int warmup, bool tracesOnly,
        string output, string? serverLabel, string schemaText, string configurationHash, JsonElement configurationSource, CancellationToken ct)
    {
        var buckets = corpus.Documents.ToDictionary(d => d.Id, d => EvaluationScope.Bucket(d.Id), StringComparer.Ordinal);
        var schemaHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(schemaText)));
        var ordered = corpus.Queries.OrderBy(q => q.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length)
            .ThenBy(q => q.Id, StringComparer.Ordinal).ToArray();
        var diagnostic = new[] { ordered[ordered.Length / 2], ordered[ordered.Length * 9 / 10] }.DistinctBy(q => q.Id).ToArray();
        List<object> traces = [];
        foreach (var configuration in configurations)
            foreach (var scope in scopes)
                foreach (var query in diagnostic)
                {
                    var (result, trace) = await SearchAsync(configuration, query, scope, profile: true);
                    if (!result.Success || trace is null)
                        throw new InvalidOperationException("Profiling failed: " + result.Error);
                    traces.Add(new { mode = configuration.Name, scopePercent = scope, queryId = query.Id, trace });
                }
        await WriteAsync("load-traces.json", new { datasetSha256 = datasetHash, schemaSha256 = schemaHash,
            configurationSha256 = configurationHash, configurations, scopes, k, traces, serverLabel });
        if (tracesOnly)
            return;

        List<(EvaluationQuery Query, int Repeat)> jobs = [];
        var random = new Random(1729);
        for (var repeat = 0; repeat < repeats; repeat++)
        {
            var queries = corpus.Queries.ToArray();
            random.Shuffle(queries);
            jobs.AddRange(queries.Select(q => (q, repeat)));
        }
        var scenarios = (from configuration in configurations from scope in scopes from concurrency in concurrencies
                         select new Scenario(configuration, scope, concurrency)).ToArray();
        new Random(1730).Shuffle(scenarios);
        List<object> results = [];
        List<object> cases = [];
        foreach (var scenario in scenarios)
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = scenario.Concurrency, CancellationToken = ct };
            var warmupFailures = 0;
            await Parallel.ForEachAsync(Enumerable.Range(0, warmup), options, async (index, _) =>
            {
                var (result, _) = await SearchAsync(scenario.Configuration, jobs[index % jobs.Count].Query, scenario.Scope);
                if (!result.Success)
                    Interlocked.Increment(ref warmupFailures);
            });
            var measurements = new Attempt[jobs.Count];
            var started = Stopwatch.GetTimestamp();
            await Parallel.ForEachAsync(Enumerable.Range(0, jobs.Count), options, async (index, _) =>
            {
                var (attempt, _) = await SearchAsync(scenario.Configuration, jobs[index].Query, scenario.Scope);
                measurements[index] = attempt;
            });
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            for (var index = 0; index < jobs.Count; index++)
            {
                var attempt = measurements[index];
                results.Add(new { mode = scenario.Configuration.Name, scopePercent = scenario.Scope,
                    concurrency = scenario.Concurrency, queryId = jobs[index].Query.Id, repeat = jobs[index].Repeat,
                    attempt.Success, attempt.ErrorCode, attempt.Error, attempt.Milliseconds, attempt.Hits });
            }
            var failures = measurements.Count(m => !m.Success);
            cases.Add(new { mode = scenario.Configuration.Name, scopePercent = scenario.Scope, concurrency = scenario.Concurrency,
                elapsedMilliseconds, attempts = jobs.Count, successful = jobs.Count - failures, warmupFailures });
            Console.WriteLine($"Load: {scenario.Configuration.Name}, scope={scenario.Scope}, concurrency={scenario.Concurrency}: {jobs.Count} attempts, {failures} failures.");
        }
        await WriteAsync("load-results.json", new
        {
            datasetSha256 = datasetHash, corpusSha256 = corpusHash, schemaSha256 = schemaHash,
            configurationSha256 = configurationHash, configurationSource, configurations, scopes, concurrencies,
            k, repeats, warmup, serverLabel, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            completedAtUtc = DateTimeOffset.UtcNow, cases, results,
            note = "Closed-loop active-request concurrency; no fixed arrival rate or external queue. Timers include native request construction, summaries and response validation, excluding embeddings, warmups, profiling and pre-slot scheduling. Failures stay in denominators with empty hits; caller cancellation and identity/scope violations abort."
        });

        async Task WriteAsync(string name, object value) => await File.WriteAllTextAsync(Path.Combine(output, name),
            JsonSerializer.Serialize(value, EvaluationCorpus.Json), ct);

        async Task<(Attempt Result, object? Trace)> SearchAsync(RetrievalSettings configuration, EvaluationQuery query, int scope, bool profile = false)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                var request = NativeRetrieval.CreateRequest(query.Text, query.Vector, "retrieval_evaluation",
                    EvaluationScope.Filter(corpusHash, scope, query), configuration, k);
                if (profile)
                {
                    request.TraceLevel = 3;
                    request.TraceExplainLevel = 2;
                    request.TraceProfileDepth = 4;
                    request.TraceQuery = false;
                }
                var response = await client.Search.SearchAsync<Dictionary<string, JsonElement>>(request, ct);
                if (response.Root.Errors is { Count: > 0 } errors)
                    return Failed("backend", string.Join("; ", errors.Select(e => e.Message)));
                var coverage = response.Root.Coverage;
                if (coverage is not { Full: true } || coverage.Degraded is { } degraded &&
                    (degraded.Timeout || degraded.AdaptiveTimeout || degraded.AnnTimeout || degraded.MatchPhase || degraded.NonIdealState))
                    return Failed("coverage", JsonSerializer.Serialize(coverage, EvaluationCorpus.Json));
                List<Hit> hits = [];
                foreach (var hit in response.Root.Children)
                {
                    if (!VespaDocumentId.TryParse(hit.FullId, out var id) || id.Namespace != "retrieval_evaluation" ||
                        id.DocumentType != "retrieval_evaluation" || !buckets.TryGetValue(id.UserSpecified, out var bucket) ||
                        bucket >= scope || id.UserSpecified == query.ExcludeDocumentId || !double.IsFinite(hit.Relevance))
                        throw new InvalidOperationException("A load result violated document identity, scope, exclusion or score constraints.");
                    hits.Add(new(id.UserSpecified, hit.Relevance));
                }
                if (hits.Count > k || hits.Select(h => h.Id).Distinct(StringComparer.Ordinal).Count() != hits.Count)
                    throw new InvalidOperationException("Duplicate or excess load results.");
                return (new(true, null, null, Stopwatch.GetElapsedTime(start).TotalMilliseconds, hits), response.Trace);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Failed("timeout", "Request timed out.");
            }
            catch (HttpRequestException error) when (!ct.IsCancellationRequested)
            {
                return Failed("transport", error.Message);
            }
            catch (VespaException error) when (!ct.IsCancellationRequested)
            {
                return Failed("vespa", error.Message);
            }

            (Attempt, object?) Failed(string code, string error) =>
                (new(false, code, error[..Math.Min(error.Length, 2000)], Stopwatch.GetElapsedTime(start).TotalMilliseconds, []), null);
        }
    }

    private sealed record Scenario(RetrievalSettings Configuration, int Scope, int Concurrency);
    private sealed record Attempt(bool Success, string? ErrorCode, string? Error, double Milliseconds, List<Hit> Hits);
    private sealed record Hit(string Id, double Score);
}
