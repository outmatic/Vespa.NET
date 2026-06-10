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
}

/// <summary>
/// Individual search hit
/// </summary>
public sealed record SearchHit<T> where T : class
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(VespaIdConverter))]
    public string Id { get; init; } = string.Empty;

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
    /// Returns the named match feature as a double, or <see langword="null"/> when
    /// the feature is missing or not a scalar (e.g. a tensor).
    /// </summary>
    public double? GetMatchFeature(string name) =>
        MatchFeatures is not null &&
        MatchFeatures.TryGetValue(name, out var value) &&
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
