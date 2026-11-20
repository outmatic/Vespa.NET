using System.Text.Json;

namespace Vespa.RetrievalEvaluation;

internal sealed record EvaluationCorpus(EvaluationDocument[] Documents, EvaluationQuery[] Queries, JsonElement Metadata)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal int Validate()
    {
        if (Documents is not { Length: > 0 } || Queries is not { Length: > 0 })
            throw new ArgumentException("Supply a nonempty document corpus and judged queries.");
        if (Metadata.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Supply dataset metadata describing its source and embeddings.");
        var ids = Documents.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != Documents.Length || ids.Any(string.IsNullOrWhiteSpace) ||
            Queries.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count() != Queries.Length)
            throw new ArgumentException("Document and query IDs must be unique and nonempty.");
        var dimensions = Documents[0].Vector?.Length ?? 0;
        var unavailable = Queries.SelectMany(q => q.Relevance.Keys).Where(id => !ids.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var declaredUnavailable = Metadata.TryGetProperty("unavailableJudgedDocumentIds", out var missing)
            ? missing.Deserialize<string[]>(Json) ?? [] : [];
        if (!unavailable.SetEquals(declaredUnavailable))
            throw new ArgumentException("Unavailable judged IDs must be explicitly recorded; retain all queries and judgments.");
        var empty = Documents.Where(d => d.Text is not null && string.IsNullOrWhiteSpace(d.Text))
            .Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var declaredEmpty = Metadata.TryGetProperty("emptyDocumentIds", out var emptyIds)
            ? emptyIds.Deserialize<string[]>(Json) ?? [] : [];
        if (!empty.SetEquals(declaredEmpty))
            throw new ArgumentException("Empty source documents must be explicitly recorded and retained.");
        if (dimensions == 0)
            throw new ArgumentException("Supply real, nonempty document and query embeddings.");
        foreach (var document in Documents)
        {
            ArgumentNullException.ThrowIfNull(document.Text);
            ValidateVector(document.Vector);
        }
        foreach (var query in Queries)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(query.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(query.Text);
            if (query.Keywords is not { Length: > 0 } || query.Keywords.Any(string.IsNullOrWhiteSpace) ||
                query.Relevance is null || !query.Relevance.Values.Any(grade => grade > 0) ||
                query.Relevance.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is < 0 or > 3))
                throw new ArgumentException("Each query needs keywords and judgments (0–3), including a positive judgment.");
            if (query.ExcludeDocumentId is { } excluded &&
                (excluded != query.Id || !ids.Contains(excluded) || query.Relevance.GetValueOrDefault(excluded) > 0))
                throw new ArgumentException("Only an existing, nonrelevant document with the identical query ID may be excluded.");
            ValidateVector(query.Vector);
        }
        return dimensions;

        void ValidateVector(float[]? vector)
        {
            if (vector is null || vector.Length != dimensions || vector.Any(v => !float.IsFinite(v)) || !vector.Any(v => v != 0))
                throw new ArgumentException("All embeddings must be finite, nonzero, and have the same dimensions.");
        }
    }
}

internal sealed record EvaluationDocument(string Id, string Text, float[] Vector, string? Title = null, string? Body = null);
internal sealed record EvaluationQuery(string Id, string Text, string[] Keywords, float[] Vector, Dictionary<string, int> Relevance,
    string? ExcludeDocumentId = null);

internal sealed class EvaluationRecord
{
    public string Id { get; set; } = "";

    public string RunId { get; set; } = "";

    public string Text { get; set; } = "";

    public ReadOnlyMemory<float> Vector { get; set; }
}
