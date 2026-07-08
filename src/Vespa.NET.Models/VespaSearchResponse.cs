using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Represents a Vespa search response
/// </summary>
public sealed record VespaSearchResponse<T> where T : class
{
    [JsonPropertyName("root")]
    public SearchRoot<T> Root { get; init; } = new();

    [JsonPropertyName("timing")]
    public TimingInfo? Timing { get; init; }

    [JsonPropertyName("trace")]
    public TraceInfo? Trace { get; init; }
}

/// <summary>
/// Root node of search results
/// </summary>
public sealed record SearchRoot<T> where T : class
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("relevance")]
    public double Relevance { get; init; }

    [JsonPropertyName("fields")]
    public SearchFields? Fields { get; init; }

    [JsonPropertyName("coverage")]
    public Coverage? Coverage { get; init; }

    [JsonPropertyName("children")]
    public List<SearchHit<T>> Children { get; init; } = [];

    [JsonPropertyName("errors")]
    public List<VespaError>? Errors { get; init; }
}

/// <summary>
/// Search result metadata fields
/// </summary>
public sealed record SearchFields
{
    [JsonPropertyName("totalCount")]
    public long TotalCount { get; init; }
}

/// <summary>
/// Coverage information for the search
/// </summary>
public sealed record Coverage
{
    [JsonPropertyName("coverage")]
    public int CoveragePercentage { get; init; }

    [JsonPropertyName("documents")]
    public long Documents { get; init; }

    [JsonPropertyName("full")]
    public bool Full { get; init; }

    [JsonPropertyName("nodes")]
    public int? Nodes { get; init; }

    [JsonPropertyName("results")]
    public int? Results { get; init; }

    [JsonPropertyName("resultsFull")]
    public int? ResultsFull { get; init; }

    /// <summary>
    /// Present when coverage is degraded; describes which mechanism caused the
    /// degradation. <see langword="null"/> when coverage is full.
    /// </summary>
    [JsonPropertyName("degraded")]
    public CoverageDegraded? Degraded { get; init; }
}

/// <summary>
/// Reasons for degraded search coverage (the <c>coverage.degraded</c> response object).
/// </summary>
public sealed record CoverageDegraded
{
    /// <summary>Coverage was reduced by match-phase degradation (schema <c>match-phase</c>).</summary>
    [JsonPropertyName("match-phase")]
    public bool MatchPhase { get; init; }

    /// <summary>The query timed out before completing.</summary>
    [JsonPropertyName("timeout")]
    public bool Timeout { get; init; }

    /// <summary>The query was stopped by adaptive timeout (soft timeout).</summary>
    [JsonPropertyName("adaptive-timeout")]
    public bool AdaptiveTimeout { get; init; }

    /// <summary>The approximate-nearest-neighbor phase timed out.</summary>
    [JsonPropertyName("anntimeout")]
    public bool AnnTimeout { get; init; }

    /// <summary>At least one content node was in a non-ideal state.</summary>
    [JsonPropertyName("non-ideal-state")]
    public bool NonIdealState { get; init; }
}

/// <summary>
/// Individual search hit
/// </summary>
public sealed record SearchHit<T> where T : class
{
    private readonly string? _id;

    /// <summary>
    /// Full hit ID exactly as returned by Vespa — a full document ID
    /// (<c>id:ns:type::doc1</c>) for document hits, or a synthetic ID
    /// (e.g. <c>group:root:0</c>, <c>index:content/0/…</c>) for other hit types.
    /// Set during deserialization.
    /// </summary>
    [JsonPropertyName("id")]
    public string FullId { get; init; } = string.Empty;

    /// <summary>
    /// User-specified (short) part of the document ID (e.g. <c>doc1</c>).
    /// Derived from <see cref="FullId"/> unless set explicitly; non-document hit IDs
    /// pass through unchanged.
    /// </summary>
    [JsonIgnore]
    public string Id
    {
        get => _id ?? VespaDocumentId.GetUserSpecified(FullId);
        init => _id = value;
    }

    [JsonPropertyName("relevance")]
    public double Relevance { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("fields")]
    public T? Fields { get; init; }

    /// <summary>
    /// Match features computed for this hit. Values can be numbers or tensors
    /// (rendered as JSON objects); use <see cref="GetMatchFeature"/> for scalars.
    /// </summary>
    [JsonPropertyName("matchfeatures")]
    public Dictionary<string, JsonElement>? MatchFeatures { get; init; }

    /// <summary>
    /// Summary features computed for this hit (schema <c>summary-features</c>).
    /// Values can be numbers or tensors (rendered as JSON objects); use
    /// <see cref="GetSummaryFeature"/> for scalars.
    /// </summary>
    [JsonPropertyName("summaryfeatures")]
    public Dictionary<string, JsonElement>? SummaryFeatures { get; init; }

    /// <summary>
    /// Returns the named match feature as a double, or <see langword="null"/> when
    /// the feature is missing or not a scalar (e.g. a tensor).
    /// </summary>
    public double? GetMatchFeature(string name) => GetScalarFeature(MatchFeatures, name);

    /// <summary>
    /// Returns the named summary feature as a double, or <see langword="null"/> when
    /// the feature is missing or not a scalar (e.g. a tensor).
    /// </summary>
    public double? GetSummaryFeature(string name) => GetScalarFeature(SummaryFeatures, name);

    private static double? GetScalarFeature(Dictionary<string, JsonElement>? features, string name) =>
        features is not null &&
        features.TryGetValue(name, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}

/// <summary>
/// Timing information for the query
/// </summary>
public sealed record TimingInfo
{
    [JsonPropertyName("querytime")]
    public double? QueryTime { get; init; }

    [JsonPropertyName("summaryfetchtime")]
    public double? SummaryFetchTime { get; init; }

    [JsonPropertyName("searchtime")]
    public double? SearchTime { get; init; }
}

/// <summary>
/// Trace information for debugging
/// </summary>
public sealed record TraceInfo
{
    [JsonPropertyName("children")]
    public List<object>? Children { get; init; }
}
