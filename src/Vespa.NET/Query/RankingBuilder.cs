using Vespa.Models;

namespace Vespa.Query;

/// <summary>
/// Fluent builder for <see cref="RankingConfig"/>.
/// <para>
/// Example:
/// <code>
/// var ranking = RankingBuilder
///     .WithProfile("semantic")
///     .Feature("query(threshold)", 0.8)
///     .Feature("query(embedding)", myTensor)
///     .RerankCount(200)
///     .Build();
/// </code>
/// </para>
/// </summary>
public sealed class RankingBuilder
{
    private string? _profile;
    private readonly Dictionary<string, object> _features = [];
    private readonly Dictionary<string, object> _properties = [];
    private int? _rerankCount;
    private bool? _listFeatures;
    private string? _sorting;
    private bool? _softTimeoutEnable;
    private double? _softTimeoutFactor;
    private string? _freshness;
    private bool? _queryCache;
    private int? _keepRankCount;
    private double? _rankScoreDropLimit;
    private int? _globalPhaseRerankCount;
    private string? _matchPhaseAttribute;
    private long? _matchPhaseMaxHits;
    private bool? _matchPhaseAscending;
    private string? _matchPhaseDiversityAttribute;
    private int? _matchPhaseDiversityMinGroups;
    private int? _matchingNumThreadsPerSearch;
    private int? _matchingMinHitsPerThread;
    private double? _matchingTermwiseLimit;
    private double? _matchingApproximateThreshold;
    private bool? _significanceUseModel;

    private RankingBuilder() { }

    /// <summary>Start with a ranking profile name</summary>
    public static RankingBuilder WithProfile(string profile) => new() { _profile = profile };

    /// <summary>Start without a preset profile (set later via <see cref="Profile"/>)</summary>
    public static RankingBuilder Create() => new();

    /// <summary>Set or change the ranking profile name</summary>
    public RankingBuilder Profile(string profile)
    {
        _profile = profile;
        return this;
    }

    /// <summary>
    /// Add a rank feature override.
    /// Use to pass query-time tensors or scalars into ranking expressions,
    /// e.g. <c>Feature("query(threshold)", 0.8)</c> or <c>Feature("query(embedding)", tensor)</c>.
    /// </summary>
    public RankingBuilder Feature(string name, object value)
    {
        _features[name] = value;
        return this;
    }

    /// <summary>
    /// Set the number of top-N first-phase documents to pass to second-phase re-ranking
    /// </summary>
    public RankingBuilder RerankCount(int count)
    {
        _rerankCount = count;
        return this;
    }

    /// <summary>
    /// Return all computed rank features per hit.
    /// Useful for debugging; avoid in production (adds overhead).
    /// </summary>
    public RankingBuilder ListAllFeatures(bool list = true)
    {
        _listFeatures = list;
        return this;
    }

    /// <summary>Override a single rank property (advanced, profile-specific)</summary>
    public RankingBuilder Property(string key, object value)
    {
        _properties[key] = value;
        return this;
    }

    /// <summary>Sort specification (e.g. "+year -relevance")</summary>
    public RankingBuilder Sorting(string spec) { _sorting = spec; return this; }

    /// <summary>Enable soft timeout with optional factor (0.0-1.0)</summary>
    public RankingBuilder SoftTimeout(bool enable = true, double? factor = null)
    {
        _softTimeoutEnable = enable;
        _softTimeoutFactor = factor;
        return this;
    }

    /// <summary>Time reference for freshness ranking (epoch seconds)</summary>
    public RankingBuilder Freshness(string time) { _freshness = time; return this; }

    /// <summary>Cache the query between ranking phases</summary>
    public RankingBuilder QueryCache(bool enable = true) { _queryCache = enable; return this; }

    /// <summary>Documents keeping rank value after match phase</summary>
    public RankingBuilder KeepRankCount(int count) { _keepRankCount = count; return this; }

    /// <summary>Minimum first-phase score threshold</summary>
    public RankingBuilder RankScoreDropLimit(double limit) { _rankScoreDropLimit = limit; return this; }

    /// <summary>Global phase rerank count</summary>
    public RankingBuilder GlobalPhaseRerankCount(int count) { _globalPhaseRerankCount = count; return this; }

    /// <summary>Configure match-phase early termination</summary>
    public RankingBuilder MatchPhase(string attribute, long? maxHits = null, bool? ascending = null)
    {
        _matchPhaseAttribute = attribute;
        _matchPhaseMaxHits = maxHits;
        _matchPhaseAscending = ascending;
        return this;
    }

    /// <summary>Configure match-phase diversity</summary>
    public RankingBuilder MatchPhaseDiversity(string attribute, int? minGroups = null)
    {
        _matchPhaseDiversityAttribute = attribute;
        _matchPhaseDiversityMinGroups = minGroups;
        return this;
    }

    /// <summary>Configure matching parameters (threads, termwise limit, approximate threshold)</summary>
    public RankingBuilder Matching(int? numThreadsPerSearch = null, int? minHitsPerThread = null, double? termwiseLimit = null, double? approximateThreshold = null)
    {
        _matchingNumThreadsPerSearch = numThreadsPerSearch;
        _matchingMinHitsPerThread = minHitsPerThread;
        _matchingTermwiseLimit = termwiseLimit;
        _matchingApproximateThreshold = approximateThreshold;
        return this;
    }

    /// <summary>Use the language model for significance estimation</summary>
    public RankingBuilder SignificanceUseModel(bool enable = true)
    {
        _significanceUseModel = enable;
        return this;
    }

    /// <summary>Build the <see cref="RankingConfig"/></summary>
    public RankingConfig Build() => new()
    {
        Profile = _profile,
        Features = _features.Count > 0 ? new Dictionary<string, object>(_features) : null,
        RerankCount = _rerankCount,
        ListFeatures = _listFeatures,
        Properties = _properties.Count > 0 ? new Dictionary<string, object>(_properties) : null,
        Sorting = _sorting,
        SoftTimeoutEnable = _softTimeoutEnable,
        SoftTimeoutFactor = _softTimeoutFactor,
        Freshness = _freshness,
        QueryCache = _queryCache,
        KeepRankCount = _keepRankCount,
        RankScoreDropLimit = _rankScoreDropLimit,
        GlobalPhaseRerankCount = _globalPhaseRerankCount,
        MatchPhaseAttribute = _matchPhaseAttribute,
        MatchPhaseMaxHits = _matchPhaseMaxHits,
        MatchPhaseAscending = _matchPhaseAscending,
        MatchPhaseDiversityAttribute = _matchPhaseDiversityAttribute,
        MatchPhaseDiversityMinGroups = _matchPhaseDiversityMinGroups,
        MatchingNumThreadsPerSearch = _matchingNumThreadsPerSearch,
        MatchingMinHitsPerThread = _matchingMinHitsPerThread,
        MatchingTermwiseLimit = _matchingTermwiseLimit,
        MatchingApproximateThreshold = _matchingApproximateThreshold,
        SignificanceUseModel = _significanceUseModel,
    };

    public static implicit operator RankingConfig(RankingBuilder builder) => builder.Build();
}
