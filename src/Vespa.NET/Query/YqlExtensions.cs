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
    /// Set the YQL query on an existing request. Apply the builder's limit and offset
    /// to <see cref="VespaSearchRequest.Hits"/> and <see cref="VespaSearchRequest.Offset"/>
    /// when the builder sets them.
    /// </summary>
    public static VespaSearchRequest WithYql(this VespaSearchRequest request, YqlBuilder builder)
    {
        request.Yql = builder.Build(includeLimitOffset: false);
        if (builder.GetLimit() is { } limit)
            request.Hits = limit;
        if (builder.GetOffset() is { } offset)
            request.Offset = offset;
        if (builder.GetUserQueryText() is { } userQuery)
            request.ModelQueryString = userQuery;
        return request;
    }

    /// <inheritdoc cref="WithYql(VespaSearchRequest, YqlBuilder)"/>
    public static VespaSearchRequest WithYql<T>(this VespaSearchRequest request, YqlBuilder<T> builder) where T : class =>
        request.WithYql(builder.Inner);

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
        request.Ranking ??= new RankingConfig();
        request.Ranking.Features ??= [];
        var key = name.Contains('(') ? name : $"query({name})";
        request.Ranking.Features[key] = value;
        return request;
    }
}
