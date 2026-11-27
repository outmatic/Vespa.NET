using System.Globalization;
using System.Text.Json;
using Vespa.Models;
using Vespa.Models.Tensors;

namespace Vespa.AgentFrameworkSample;

// This recipe uses the existing native client. Query parsing, fieldsets, BM25,
// normalization, RRF and ranking execute in Vespa, including during evaluation.
internal sealed record RetrievalSettings
{
    public string Name { get; init; } = "semantic";
    public string Mode { get; init; } = "semantic";
    public int VectorCandidates { get; init; } = 100;
    public int KeywordCandidates { get; init; } = 100;
    public double VectorWeight { get; init; } = 0.5;
    public double TitleWeight { get; init; } = 1;
    public int RrfK { get; init; } = 60;
    public string? Language { get; init; } = "en";
    public string TextFieldSet { get; init; } = "default";
    public string Grammar { get; init; } = "weakAnd";
    public string[] ResultFields { get; init; } = ["title", "body"];
    public string? SummaryClass { get; init; }
    public string? RankProfileOverride { get; init; }
    public bool Approximate { get; init; } = true;
    public int? HnswExploreAdditionalHits { get; init; }
    public int? GlobalPhaseRerankCount { get; init; }
    public double? WeakAndStopwordLimit { get; init; }
    public double? WeakAndAdjustTarget { get; init; }

    // Keep sample defaults separate from settings loaded by historical evaluations.
    internal static RetrievalSettings ForSample(string? mode = null)
    {
        var selected = mode ?? "semantic";
        return new()
        {
            Name = selected,
            Mode = selected switch
            {
                "lexical" => "lexical",
                "semantic" => "semantic",
                "hybrid" => "hybrid_normalized",
                "rrf" => "hybrid_rrf",
                _ => throw new ArgumentException("Choose --retrieval lexical, semantic, hybrid or rrf.")
            },
            Grammar = "tokenize",
            GlobalPhaseRerankCount = selected is "hybrid" or "rrf" ? 100 : null
        };
    }

    internal string RankProfile => RankProfileOverride ?? (Mode switch
    {
        "lexical" => "lexical",
        "semantic" => "vector_search",
        "hybrid_normalized" => "hybrid_normalized",
        "hybrid_rrf" => "hybrid_rrf_" + RrfK.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("Choose lexical, semantic, hybrid_normalized or hybrid_rrf.")
    });

    internal void Validate(int hits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (Mode is not ("lexical" or "semantic" or "hybrid_normalized" or "hybrid_rrf"))
            throw new ArgumentException("Unsupported retrieval mode.");
        _ = RankProfile;
        if (!IsIdentifier(TextFieldSet) || ResultFields is not { Length: > 0 } || ResultFields.Any(f => !IsIdentifier(f)))
            throw new ArgumentException("TextFieldSet and ResultFields must contain native field identifiers.");
        if (Grammar is not ("weakAnd" or "tokenize" or "all" or "any" or "phrase" or "raw"))
            throw new ArgumentException("Unsupported native query grammar.");
        if (Language is not null && string.IsNullOrWhiteSpace(Language) ||
            SummaryClass is not null && string.IsNullOrWhiteSpace(SummaryClass) ||
            RankProfileOverride is not null && string.IsNullOrWhiteSpace(RankProfileOverride))
            throw new ArgumentException("Optional language, summary and profile names cannot be blank.");
        if (HnswExploreAdditionalHits is < 0)
            throw new ArgumentOutOfRangeException(nameof(HnswExploreAdditionalHits));
        if (GlobalPhaseRerankCount is { } rerank &&
            (rerank < hits || rerank > 2000 || !Mode.StartsWith("hybrid_", StringComparison.Ordinal)))
            throw new ArgumentException("GlobalPhaseRerankCount requires hybrid mode and hits <= count <= 2000.");
        if (!IsFraction(WeakAndStopwordLimit) || !IsFraction(WeakAndAdjustTarget) ||
            (WeakAndStopwordLimit is not null || WeakAndAdjustTarget is not null) &&
            (Mode == "semantic" || Grammar is not ("weakAnd" or "tokenize")))
            throw new ArgumentException("WeakAnd tuning requires lexical/hybrid weakAnd or tokenize grammar and finite values in [0,1].");
        if (hits is < 1 or > 100 || VectorCandidates < hits || KeywordCandidates < hits ||
            VectorCandidates > 1000 || KeywordCandidates > 1000)
            throw new ArgumentException("Require 1 <= hits <= 100 and hits <= each candidate target <= 1000.");
        if (!double.IsFinite(VectorWeight) || VectorWeight is < 0 or > 1 || !double.IsFinite(TitleWeight) || TitleWeight is <= 0 or > 10)
            throw new ArgumentException("VectorWeight must be in [0,1] and TitleWeight in (0,10].");
        if (RrfK is not (10 or 30 or 60 or 100))
            throw new ArgumentException("The example deploys RRF profiles for k=10,30,60,100.");
    }

    internal static bool IsIdentifier(string? value) => !string.IsNullOrEmpty(value) && value.Split('.').All(part =>
        part.Length > 0 && (char.IsAsciiLetter(part[0]) || part[0] == '_') &&
        part.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));

    private static bool IsFraction(double? value) => value is null || double.IsFinite(value.Value) && value is >= 0 and <= 1;
}

internal static class NativeRetrieval
{
    // schema and filter come from trusted application code, never from a model.
    internal static VespaSearchRequest CreateRequest(string query, float[]? vector, string schema, string filter,
        RetrievalSettings settings, int hits = 3, bool? approximate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        settings.Validate(hits);
        if (string.IsNullOrEmpty(schema) || !(char.IsAsciiLetter(schema[0]) || schema[0] == '_') ||
            schema.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Expected a simple schema identifier.", nameof(schema));
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);
        if (settings.Mode != "lexical" && (vector is not { Length: > 0 } || vector.Any(v => !float.IsFinite(v)) || !vector.Any(v => v != 0)))
            throw new ArgumentException("Semantic and hybrid retrieval require a finite, nonzero query vector.", nameof(vector));

        var lexical = string.Create(CultureInfo.InvariantCulture,
            $"({{grammar:{JsonSerializer.Serialize(settings.Grammar)},defaultIndex:{JsonSerializer.Serialize(settings.TextFieldSet)},totalTargetHits:{settings.KeywordCandidates}}}userInput(@lexical_query))");
        var useApproximate = approximate ?? settings.Approximate;
        var exploration = useApproximate && settings.HnswExploreAdditionalHits is { } additional
            ? ",hnsw.exploreAdditionalHits:" + additional.ToString(CultureInfo.InvariantCulture) : "";
        var nearest = string.Create(CultureInfo.InvariantCulture,
            $"({{totalTargetHits:{settings.VectorCandidates},approximate:{(useApproximate ? "true" : "false")}{exploration}}}nearestNeighbor(embedding, query_embedding))");
        var retrieval = settings.Mode switch
        {
            "lexical" => lexical,
            "semantic" => nearest,
            _ => $"({nearest} or {lexical})"
        };
        var input = new Dictionary<string, object?>
        {
            ["query(vector_weight)"] = settings.VectorWeight,
            ["query(title_weight)"] = settings.TitleWeight
        };
        if (settings.Mode != "lexical")
            input["query(query_embedding)"] = VespaTensor.FromDenseValues(vector!);
        var request = new VespaSearchRequest
        {
            Yql = $"select {string.Join(", ", new[] { "documentid" }.Concat(settings.ResultFields).Distinct(StringComparer.Ordinal))} from sources {schema} where ({filter}) and ({retrieval})",
            Hits = hits,
            ModelLanguage = settings.Language,
            PresentationSummary = settings.SummaryClass,
            Ranking = new()
            {
                Profile = settings.RankProfile,
                GlobalPhaseRerankCount = settings.Mode.StartsWith("hybrid_", StringComparison.Ordinal)
                    ? settings.GlobalPhaseRerankCount ?? settings.VectorCandidates + settings.KeywordCandidates : null
            },
            Input = input
        }.WithTimeout(TimeSpan.FromSeconds(30));
        if (settings.Mode != "semantic")
        {
            request = request.WithParameter("lexical_query", query);
            if (settings.WeakAndStopwordLimit is { } stopwordLimit)
                request = request.WithParameter("ranking.matching.weakand.stopwordLimit", stopwordLimit);
            if (settings.WeakAndAdjustTarget is { } adjustTarget)
                request = request.WithParameter("ranking.matching.weakand.adjustTarget", adjustTarget);
        }
        return request;
    }
}
