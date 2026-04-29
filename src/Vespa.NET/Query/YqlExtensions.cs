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
    public static VespaSearchRequest ToSearchRequest(this YqlBuilder builder)
    {
        var request = new VespaSearchRequest { Yql = builder.Build(includeLimitOffset: false) };
        if (builder.GetLimit() is { } limit)
            request.Hits = limit;
        if (builder.GetOffset() is { } offset)
            request.Offset = offset;
        return request;
    }

    /// <inheritdoc cref="ToSearchRequest(YqlBuilder)"/>
    public static VespaSearchRequest ToSearchRequest<T>(this YqlBuilder<T> builder) where T : class
    {
        var request = new VespaSearchRequest { Yql = builder.Build(includeLimitOffset: false) };
        if (builder.GetLimit() is { } limit)
            request.Hits = limit;
        if (builder.GetOffset() is { } offset)
            request.Offset = offset;
        return request;
    }

    /// <summary>
    /// Set the YQL query on an existing request from a <see cref="YqlBuilder"/>
    /// </summary>
    public static VespaSearchRequest WithYql(this VespaSearchRequest request, YqlBuilder builder)
    {
        request.Yql = builder.Build();
        return request;
    }

    /// <inheritdoc cref="WithYql(VespaSearchRequest, YqlBuilder)"/>
    public static VespaSearchRequest WithYql<T>(this VespaSearchRequest request, YqlBuilder<T> builder) where T : class
    {
        request.Yql = builder.Build();
        return request;
    }

    /// <summary>
    /// Apply a <see cref="RankingBuilder"/> configuration to the request
    /// </summary>
    public static VespaSearchRequest WithRanking(this VespaSearchRequest request, RankingBuilder builder)
    {
        request.Ranking = builder.Build();
        return request;
    }

    // ── Fluent helpers on VespaSearchRequest ──────────────────────────────────

    /// <summary>
    /// Set the ranking profile name.
    /// </summary>
    public static VespaSearchRequest WithRankProfile(this VespaSearchRequest request, string profile)
    {
        request.Ranking ??= new RankingConfig();
        request.Ranking.Profile = profile;
        return request;
    }

    /// <summary>
    /// Set a query tensor input: <c>Input["query({name})"] = value</c>.
    /// Use for <c>nearestNeighbor</c> query vectors or <c>embed()</c> expressions.
    /// </summary>
    /// <param name="request">The search request.</param>
    /// <param name="tensorName">Tensor parameter name (e.g. <c>"q"</c> → <c>query(q)</c>).</param>
    /// <param name="value">Tensor value — a <c>VespaTensor</c>, a float array, or an embed expression string.</param>
    public static VespaSearchRequest WithQueryTensor(this VespaSearchRequest request, string tensorName, object value)
    {
        request.Input ??= [];
        request.Input[$"query({tensorName})"] = value;
        return request;
    }

    /// <summary>
    /// Set a <c>userInput(@param)</c> value: <c>CustomParameters[param] = value</c>.
    /// </summary>
    public static VespaSearchRequest WithUserInput(this VespaSearchRequest request, string paramName, object value)
    {
        request.CustomParameters ??= [];
        request.CustomParameters[paramName] = value;
        return request;
    }

    /// <summary>
    /// Set a rank feature: <c>Ranking.Features["query({name})"] = value</c>.
    /// </summary>
    public static VespaSearchRequest WithRankFeature(this VespaSearchRequest request, string name, object value)
    {
        request.Ranking ??= new RankingConfig();
        request.Ranking.Features ??= [];
        request.Ranking.Features[$"query({name})"] = value;
        return request;
    }
}
