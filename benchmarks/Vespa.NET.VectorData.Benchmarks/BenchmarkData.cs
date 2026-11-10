using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vespa.VectorData.Benchmarks;

internal sealed record CorpusDocument(string Id, float[] Vector, int Bucket, string Text);
internal sealed record Corpus(CorpusDocument[] Documents, float[][] Queries)
{
    internal static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal int Dimensions => Documents[0].Vector.Length;
    internal string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, Json)));

    internal void Validate()
    {
        if (Documents is null || Queries is null || Documents.Length == 0 || Queries.Length == 0)
            throw new ArgumentException("The corpus must contain documents and separate query vectors.");
        if (Documents.Any(d => d is null || string.IsNullOrWhiteSpace(d.Id) || d.Vector is null || d.Text is null || d.Bucket is < 0 or > 99) ||
            Documents.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != Documents.Length)
            throw new ArgumentException("Documents require unique nonempty IDs, vectors, text and buckets in [0,99].");
        foreach (var vector in Documents.Select(d => d.Vector).Concat(Queries))
            if (vector is null || vector.Length != Dimensions || Dimensions == 0 ||
                vector.Any(v => !float.IsFinite(v)) || !vector.Any(v => v != 0))
                throw new ArgumentException("All vectors must be finite, nonzero, and have the same dimensions.");
    }

    // Clustered Gaussian vectors exercise a nonuniform index but are not language embeddings.
    internal static Corpus Synthetic(int count, int dimensions, int queryCount, int seed)
    {
        var random = new Random(seed);
        var centers = Enumerable.Range(0, 32).Select(_ => UnitVector()).ToArray();
        var documents = Enumerable.Range(0, count).Select(i => new CorpusDocument(
            i.ToString(CultureInfo.InvariantCulture), ClusterVector(), random.Next(100),
            $"Synthetic benchmark passage {i}. " + new string('x', 256))).ToArray();
        return new(documents, Enumerable.Range(0, queryCount).Select(_ => ClusterVector()).ToArray());

        float[] UnitVector()
        {
            var vector = Enumerable.Range(0, dimensions).Select(_ => Gaussian()).ToArray();
            Normalize(vector);
            return vector;
        }

        float[] ClusterVector()
        {
            var center = centers[random.Next(centers.Length)];
            var vector = center.Select(value => value + 0.5f * Gaussian() / MathF.Sqrt(dimensions)).ToArray();
            Normalize(vector);
            return vector;
        }

        float Gaussian() => (float)(Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble()));
        static void Normalize(float[] vector)
        {
            var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
            for (var i = 0; i < vector.Length; i++)
                vector[i] = (float)(vector[i] / norm);
        }
    }
}

internal sealed record Settings(string Endpoint, string? ConfigEndpoint, bool Deploy, bool Feed, string? Dataset,
    string Output, string ServerLabel, int Documents, int Dimensions, int Queries, int Seed, int K, int Repeats,
    int Warmup, int[] Explore, int[] Selectivity, string[] Summaries, int FeedConcurrency)
{
    internal static Settings Parse(string[] args)
    {
        var switches = new HashSet<string>(["--deploy", "--feed"], StringComparer.Ordinal);
        var names = new HashSet<string>(["--endpoint", "--config-endpoint", "--dataset", "--out", "--server-label",
            "--documents", "--dimensions", "--queries", "--seed", "--k", "--repeats", "--warmup", "--explore", "--selectivity", "--summaries", "--feed-concurrency"], StringComparer.Ordinal);
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!switches.Contains(key) && !names.Contains(key))
                throw new ArgumentException($"Unknown option: {key}. Use --help.");
            var value = switches.Contains(key) ? "true" : ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value for {key}.");
            if (!values.TryAdd(key, value))
                throw new ArgumentException($"Duplicate option: {key}.");
        }
        string Get(string name, string fallback) => values.GetValueOrDefault(name, fallback);
        int Number(string name, int fallback, int min = 1)
        {
            var value = int.Parse(Get(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
            return value >= min ? value : throw new ArgumentException($"{name} must be >= {min}.");
        }
        int[] Numbers(string name, string fallback, int min, int max) => Get(name, fallback).Split(',').Select(v =>
        {
            var value = int.Parse(v, CultureInfo.InvariantCulture);
            return value >= min && value <= max ? value : throw new ArgumentException($"{name} values must be in [{min},{max}].");
        }).Distinct().ToArray();
        var endpoint = Get("--endpoint", "");
        ValidateEndpoint(endpoint);
        var config = values.GetValueOrDefault("--config-endpoint");
        if (config is not null)
            ValidateEndpoint(config);
        if (values.ContainsKey("--deploy") && config is null)
            throw new ArgumentException("--deploy requires an explicit --config-endpoint. Deployment replaces the whole application.");
        var summaries = Get("--summaries", "vector_data,default").Split(',').Distinct().ToArray();
        if (summaries.Any(s => s is not ("vector_data" or "default")))
            throw new ArgumentException("--summaries accepts vector_data and/or default.");
        var k = Number("--k", 10);
        if (k > 400)
            throw new ArgumentException("--k is bounded to 400 by the benchmark query profile contract.");
        return new(endpoint, config, values.ContainsKey("--deploy"), values.ContainsKey("--feed"), values.GetValueOrDefault("--dataset"),
            Get("--out", Path.Combine("artifacts", "vector-search-benchmarks", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8])),
            Get("--server-label", "unspecified (record image digest/server version for reproducibility)"),
            Number("--documents", 10000), Number("--dimensions", 128), Number("--queries", 100), Number("--seed", 1729, 0),
            k, Number("--repeats", 3), Number("--warmup", 5, 0), Numbers("--explore", "0,32,64,128", 0, int.MaxValue),
            Numbers("--selectivity", "100,10,1", 1, 100), summaries, Number("--feed-concurrency", 16));
    }

    private static void ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Supply an explicit HTTP(S) endpoint without credentials, query, or fragment.");
    }
}

internal static class Metrics
{
    internal static double Recall(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var truth = expected.ToHashSet(StringComparer.Ordinal);
        if (truth.Count == 0)
            throw new ArgumentException("Recall is undefined for an empty ground-truth set.");
        return (double)actual.Distinct(StringComparer.Ordinal).Count(truth.Contains) / truth.Count;
    }

    // Nearest-rank empirical percentile; no interpolation, and meaningful only with enough samples.
    internal static double Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0 || !double.IsFinite(fraction) || fraction is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(fraction));
        return sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];
    }

    internal static void SelfTest()
    {
        if (Recall(["a", "b"], ["a", "a", "c"]) != 0.5 || Recall(["a"], []) != 0 ||
            Percentile([4, 1, 3, 2], 0.5) != 2 || Percentile(Enumerable.Range(1, 100).Select(i => (double)i), 0.95) != 95)
            throw new InvalidOperationException("Metric self-test failed.");
        var first = Corpus.Synthetic(10, 8, 3, 42);
        first.Validate();
        var decoded = JsonSerializer.Deserialize<Corpus>(JsonSerializer.Serialize(first, Corpus.Json), Corpus.Json)!;
        decoded.Validate();
        if (decoded.Fingerprint != first.Fingerprint)
            throw new InvalidOperationException("External dataset JSON round-trip self-test failed.");
        if (first.Fingerprint != Corpus.Synthetic(10, 8, 3, 42).Fingerprint || first.Fingerprint == Corpus.Synthetic(10, 8, 3, 43).Fingerprint)
            throw new InvalidOperationException("Dataset determinism self-test failed.");
        Console.WriteLine("Metric and deterministic-dataset self-tests passed.");
    }
}
