using System.Collections.Immutable;
using Vespa.Models;

namespace Vespa.Query;

/// <summary>
/// Extension methods integrating query builders with <see cref="VespaSearchRequest"/>
/// </summary>
public static class YqlExtensions
{
    /// <summary>
    /// Create a <see cref="VespaSearchRequest"/> from a <see cref="YqlBuilder"/>,
    /// capturing YQL, Hits (from Limit), and Offset.
    /// </summary>
    public static VespaSearchRequest ToSearchRequest(this YqlBuilder builder) =>
        new VespaSearchRequest().WithYql(builder);

    /// <inheritdoc cref="ToSearchRequest(YqlBuilder)"/>
    public static VespaSearchRequest ToSearchRequest<T>(this YqlBuilder<T> builder) where T : class =>
        builder.Inner.ToSearchRequest();

    /// <summary>
    /// Returns a request with the builder's YQL query. Applies its limit and offset
    /// to <see cref="VespaSearchRequest.Hits"/> and <see cref="VespaSearchRequest.Offset"/>
    /// when the builder sets them.
    /// </summary>
    public static VespaSearchRequest WithYql(this VespaSearchRequest request, YqlBuilder builder) =>
        request with
        {
            Yql = builder.Build(includeLimitOffset: false),
            Hits = builder.GetLimit() ?? request.Hits,
            Offset = builder.GetOffset() ?? request.Offset,
            ModelQueryString = builder.GetUserQueryText() ?? request.ModelQueryString
        };

    /// <inheritdoc cref="WithYql(VespaSearchRequest, YqlBuilder)"/>
    public static VespaSearchRequest WithYql<T>(this VespaSearchRequest request, YqlBuilder<T> builder) where T : class =>
        request.WithYql(builder.Inner);

    /// <summary>
    /// Apply a <see cref="RankingBuilder"/> configuration to the request
    /// </summary>
    public static VespaSearchRequest WithRanking(this VespaSearchRequest request, RankingBuilder builder) =>
        request with { Ranking = builder.Build() };

    // ── Fluent helpers on VespaSearchRequest ──────────────────────────────────

    /// <summary>
    /// Set the ranking profile name.
    /// </summary>
    public static VespaSearchRequest WithRankProfile(this VespaSearchRequest request, string profile) =>
        request with { Ranking = (request.Ranking ?? new RankingConfig()) with { Profile = profile } };

    /// <summary>
    /// Set a query tensor input: <c>Input["query({name})"] = value</c>.
    /// Use for <c>nearestNeighbor</c> query vectors or <c>embed()</c> expressions.
    /// </summary>
    /// <param name="request">The search request.</param>
    /// <param name="tensorName">Tensor parameter name (e.g. <c>"q"</c> → <c>query(q)</c>).</param>
    /// <param name="value">Tensor value — a <c>VespaTensor</c>, a float array, or an embed expression string.</param>
    public static VespaSearchRequest WithQueryTensor(this VespaSearchRequest request, string tensorName, object value) =>
        request with
        {
            Input = (SearchParameterMap.Freeze(request.Input) ?? ImmutableDictionary<string, object?>.Empty)
                .SetItem($"query({tensorName})", value)
        };

    /// <summary>
    /// Set the top-level request parameter referenced by <c>userInput(@param)</c>.
    /// </summary>
    public static VespaSearchRequest WithUserInput(this VespaSearchRequest request, string paramName, object value) =>
        request.WithParameter(paramName, value);

    /// <summary>
    /// Set a rank feature: <c>Ranking.Features["query({name})"] = value</c>.
    /// A bare name is wrapped as <c>query(name)</c>; a full feature reference that
    /// already contains parentheses (e.g. <c>"query(x)"</c>, the form
    /// <c>RankingBuilder.Feature</c> takes) is used as-is.
    /// </summary>
    public static VespaSearchRequest WithRankFeature(this VespaSearchRequest request, string name, object value)
    {
        var ranking = request.Ranking ?? new RankingConfig();
        var key = name.Contains('(') ? name : $"query({name})";
        var features = SearchParameterMap.Freeze(ranking.Features) ?? ImmutableDictionary<string, object>.Empty;
        return request with { Ranking = ranking with { Features = features.SetItem(key, value) } };
    }
}
