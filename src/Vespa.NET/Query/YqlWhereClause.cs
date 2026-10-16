using System.Linq.Expressions;
using Vespa.Models.Attributes;

namespace Vespa.Query;

/// <summary>
/// Fluent builder for a YQL WHERE clause
/// </summary>
public sealed class YqlWhereClause
{
    private readonly List<YqlPredicate> _andPredicates = [];

    internal YqlWhereClause() { }

    /// <summary>Start building a predicate on the named field</summary>
    public YqlFieldClause Field(string fieldName) =>
        new(YqlIdentifier.Validate(fieldName, nameof(fieldName)), this);

    /// <summary>
    /// Start building a predicate on the field whose name is resolved from
    /// <c>[VespaField(Name = "...")]</c> on the selected property (falls back to the property name).
    /// </summary>
    public YqlFieldClause Field<T>(Expression<Func<T, object?>> selector) where T : class
        => Field(VespaDocumentMeta.FieldName(selector));

    /// <summary>
    /// Creates and configures a nested clause, bubbling up state that must live on
    /// the top-level clause (the <c>userQuery()</c> text for <c>model.queryString</c>).
    /// </summary>
    private YqlWhereClause BuildSubClause(Action<YqlWhereClause> configure)
    {
        var sub = new YqlWhereClause();
        configure(sub);
        UserQueryText ??= sub.UserQueryText;
        return sub;
    }

    /// <summary>Logical AND with a sub-expression</summary>
    public YqlWhereClause And(Action<YqlWhereClause> subClause)
        => AddPredicate(BuildSubClause(subClause).BuildPredicate());

    /// <summary>
    /// <c>pred1 or pred2 or pred3</c> — matches documents satisfying any of the given predicates.
    /// Each callback builds a single predicate; they are OR'd together at the same level.
    /// </summary>
    public YqlWhereClause AnyOf(params Action<YqlWhereClause>[] predicates)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(predicates.Length, 1, nameof(predicates));
        if (predicates.Length == 1)
        {
            predicates[0](this);
            return this;
        }
        var built = predicates.Select(configure => BuildSubClause(configure).BuildPredicate()).ToList();
        return AddPredicate(new YqlPredicate.Or(built));
    }

    /// <summary>
    /// <c>existing or (sub-predicates OR'd together)</c> — ORs existing predicates with the
    /// sub-expression. Multiple predicates in the callback are OR'd (not AND'd).
    /// </summary>
    public YqlWhereClause Or(Action<YqlWhereClause> subClause)
    {
        var sub = BuildSubClause(subClause);
        var subPredicates = sub.GetPredicates();
        var existing = _andPredicates.ToList();
        _andPredicates.Clear();
        // Merge all predicates into a single flat OR
        var allOperands = new List<YqlPredicate>();
        if (existing.Count == 1)
            allOperands.Add(existing[0]);
        else if (existing.Count > 1)
            allOperands.Add(new YqlPredicate.And(existing));
        allOperands.AddRange(subPredicates);
        return AddPredicate(allOperands.Count == 1 ? allOperands[0] : new YqlPredicate.Or(allOperands));
    }

    /// <summary>
    /// <c>!(expr)</c> — logical negation of the sub-expression: matches documents
    /// that do <em>not</em> satisfy it. Multiple predicates in the callback are
    /// AND'd before negation.
    /// </summary>
    public YqlWhereClause Not(Action<YqlWhereClause> subClause)
        => AddPredicate(new YqlPredicate.Not(BuildSubClause(subClause).BuildPredicate()));

    /// <summary>
    /// Embeds a raw YQL predicate fragment verbatim.
    /// <para>
    /// <b>Warning:</b> the fragment is <em>not</em> validated or escaped — passing
    /// unvalidated end-user input here is a YQL injection risk. Use the typed
    /// predicate methods whenever possible; reserve this for operators or
    /// annotations the builder does not cover.
    /// </para>
    /// </summary>
    public YqlWhereClause Raw(string yqlPredicate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yqlPredicate);
        return AddPredicate(new YqlPredicate.RawPredicate(yqlPredicate));
    }

    /// <summary>Match all documents</summary>
    public YqlWhereClause True()
        => AddPredicate(new YqlPredicate.True());

    /// <summary>
    /// Nearest-neighbor predicate with optional annotations.
    /// Prefer <paramref name="totalTargetHits"/> (hits over all content nodes) over the
    /// per-node <paramref name="targetHits"/>; when set, it replaces the targetHits annotation.
    /// </summary>
    public YqlWhereClause NearestNeighbor(
        string field, string queryTensor, int targetHits = 10,
        string? label = null, bool? approximate = null,
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null,
        int? totalTargetHits = null, int? minTargetHits = null)
    {
        YqlIdentifier.Validate(field, nameof(field));
        YqlIdentifier.Validate(queryTensor, nameof(queryTensor));
        return AddPredicate(new YqlPredicate.NearestNeighbor(field, queryTensor, targetHits, label, approximate, distanceThreshold, hnswExploreAdditionalHits, totalTargetHits, minTargetHits));
    }

    /// <summary>
    /// Nearest-neighbor predicate — field name resolved via <c>[VespaField]</c>
    /// on the selected property.
    /// </summary>
    public YqlWhereClause NearestNeighbor<T>(
        Expression<Func<T, object?>> fieldSelector,
        string queryTensor,
        int targetHits = 10,
        string? label = null, bool? approximate = null,
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null,
        int? totalTargetHits = null, int? minTargetHits = null) where T : class
        => NearestNeighbor(VespaDocumentMeta.FieldName(fieldSelector), queryTensor, targetHits, label, approximate, distanceThreshold, hnswExploreAdditionalHits, totalTargetHits, minTargetHits);

    /// <summary>
    /// <c>[{targetHits:N}]weakAnd(pred1, pred2, ...)</c> — scores documents by weakly combining
    /// all matching sub-predicates; only the top hits are returned.
    /// </summary>
    /// <param name="configure">Callback that builds the sub-predicates.</param>
    /// <param name="targetHits">Optional per-node optimizer hint; prefer <paramref name="totalTargetHits"/>.</param>
    /// <param name="totalTargetHits">Wanted hits over all content nodes (preferred); replaces <paramref name="targetHits"/> when set.</param>
    public YqlWhereClause WeakAnd(Action<YqlWhereClause> configure, int? targetHits = null, int? totalTargetHits = null)
    {
        var sub = BuildSubClause(configure);
        var predicates = sub.GetPredicates();
        if (predicates.Count == 0)
            throw new ArgumentException("weakAnd requires at least one predicate.", nameof(configure));
        return AddPredicate(new YqlPredicate.WeakAnd(predicates, targetHits, totalTargetHits));
    }

    /// <summary>
    /// <c>{targetHits:N}wand(field, {{"term": weight}})</c> — retrieves the top
    /// <paramref name="targetHits"/> documents by weighted term-set dot product.
    /// Prefer <paramref name="totalTargetHits"/> (over all content nodes); when set,
    /// it replaces the per-node targetHits annotation.
    /// </summary>
    public YqlWhereClause Wand(string field, IReadOnlyDictionary<string, int> terms, int targetHits = 100, double? scoreThreshold = null, int? totalTargetHits = null)
    {
        YqlIdentifier.Validate(field, nameof(field));
        return AddPredicate(new YqlPredicate.Wand(field, terms, targetHits, scoreThreshold, totalTargetHits));
    }

    /// <summary>
    /// <c>dotProduct(field, {{"term": weight}})</c> — scores documents by the dot
    /// product of the weighted term set against the field's term vector.
    /// </summary>
    public YqlWhereClause DotProduct(string field, IReadOnlyDictionary<string, int> terms)
    {
        YqlIdentifier.Validate(field, nameof(field));
        return AddPredicate(new YqlPredicate.DotProduct(field, terms));
    }

    /// <summary>
    /// <c>geoLocation(field, lat, lon, "Xkm")</c> — filters documents within
    /// <paramref name="radiusKm"/> kilometres of the given coordinates.
    /// Requires a Vespa <c>position</c> field type.
    /// </summary>
    public YqlWhereClause GeoLocation(string field, double latitude, double longitude, double radiusKm)
    {
        YqlIdentifier.Validate(field, nameof(field));
        return AddPredicate(new YqlPredicate.GeoLocation(field, latitude, longitude, radiusKm));
    }

    /// <summary>
    /// <c>userQuery()</c> — interprets free-text user input using Vespa's simple query
    /// language. Safe to use directly with end-user input: the text is not embedded in
    /// the YQL but sent via the <c>model.queryString</c> request parameter, set
    /// automatically when the builder is converted with <c>ToSearchRequest()</c> or
    /// <c>WithYql()</c>. When building the YQL string manually, set
    /// <c>VespaSearchRequest.ModelQueryString</c> yourself.
    /// </summary>
    public YqlWhereClause UserQuery(string query)
    {
        UserQueryText = query;
        return AddPredicate(new YqlPredicate.UserQuery());
    }

    /// <summary>Free text captured by <see cref="UserQuery"/>, sent as <c>model.queryString</c>.</summary>
    internal string? UserQueryText { get; private set; }

    /// <summary>
    /// <c>userInput(@param)</c> — references a query parameter containing user input.
    /// Pass the actual text via <c>request.WithParameter("param", text)</c> at query time.
    /// </summary>
    public YqlWhereClause UserInput(string paramName, string? grammar = null, string? defaultIndex = null, string? language = null, bool? allowEmpty = null)
    {
        YqlIdentifier.Validate(paramName, nameof(paramName));
        return AddPredicate(new YqlPredicate.UserInput(paramName, grammar, defaultIndex, language, allowEmpty));
    }

    /// <summary>
    /// Standard Vespa hybrid search pattern:
    /// <c>rank({targetHits:N}nearestNeighbor(field, tensor), userInput(@param))</c>.
    /// Nearest-neighbor retrieves documents; userInput contributes text-matching features for ranking.
    /// </summary>
    /// <param name="vectorField">The tensor field to search (e.g. "embedding").</param>
    /// <param name="queryTensor">The query tensor name (e.g. "q").</param>
    /// <param name="userInputParam">The userInput parameter name (without @).</param>
    /// <param name="targetHits">Target number of nearest-neighbor hits per node; prefer <paramref name="totalTargetHits"/>.</param>
    /// <param name="label">Optional label for the nearest-neighbor operator.</param>
    /// <param name="approximate">Whether to use approximate nearest-neighbor search.</param>
    /// <param name="distanceThreshold">Maximum distance threshold.</param>
    /// <param name="hnswExploreAdditionalHits">Extra HNSW exploration hits.</param>
    /// <param name="totalTargetHits">Wanted hits over all content nodes (preferred); replaces <paramref name="targetHits"/> when set.</param>
    public YqlWhereClause HybridSearch(
        string vectorField, string queryTensor, string userInputParam,
        int targetHits = 100,
        string? label = null, bool? approximate = null,
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null,
        int? totalTargetHits = null)
    {
        YqlIdentifier.Validate(vectorField, nameof(vectorField));
        YqlIdentifier.Validate(queryTensor, nameof(queryTensor));
        YqlIdentifier.Validate(userInputParam, nameof(userInputParam));
        var nn = new YqlPredicate.NearestNeighbor(vectorField, queryTensor, targetHits, label, approximate, distanceThreshold, hnswExploreAdditionalHits, totalTargetHits);
        var ui = new YqlPredicate.UserInput(userInputParam);
        return AddPredicate(new YqlPredicate.HybridSearch(nn, ui));
    }

    /// <summary>Match no documents</summary>
    public YqlWhereClause False()
        => AddPredicate(new YqlPredicate.False());

    /// <summary>
    /// <c>rank(matchExpr, rankExpr1, rankExpr2, ...)</c> — first argument determines which
    /// documents match; all arguments contribute to ranking.
    /// </summary>
    public YqlWhereClause Rank(params Action<YqlWhereClause>[] clauses)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clauses.Length, 1, nameof(clauses));
        if (clauses.Length == 1)
        {
            clauses[0](this);
            return this;
        }
        var predicates = clauses.Select(configure => BuildSubClause(configure).BuildPredicate()).ToList();
        return AddPredicate(new YqlPredicate.Rank(predicates));
    }

    /// <summary>
    /// <c>nonEmpty(expr)</c> — validates that the sub-expression is not empty after
    /// parameter substitution.
    /// </summary>
    public YqlWhereClause NonEmpty(Action<YqlWhereClause> configure)
        => AddPredicate(new YqlPredicate.NonEmpty(BuildSubClause(configure).BuildPredicate()));

    /// <summary>
    /// <c>geoBoundingBox(field, south, west, north, east)</c> — position inside a
    /// geographical rectangle: south-western corner (minimum latitude/longitude)
    /// first, then north-eastern corner.
    /// </summary>
    public YqlWhereClause GeoBoundingBox(string field, double south, double west, double north, double east)
    {
        YqlIdentifier.Validate(field, nameof(field));
        return AddPredicate(new YqlPredicate.GeoBoundingBox(field, south, west, north, east));
    }

    /// <summary>
    /// <c>weightedSet(field, {{"token": weight}})</c> — matches documents containing
    /// any of the tokens, scoring by the largest matched weight.
    /// </summary>
    public YqlWhereClause WeightedSet(string field, IReadOnlyDictionary<string, int> tokens)
    {
        YqlIdentifier.Validate(field, nameof(field));
        return AddPredicate(new YqlPredicate.WeightedSet(field, tokens));
    }

    /// <summary>
    /// <c>predicate(field, attributes, ranges)</c> — queries a predicate field.
    /// Empty attribute/range maps are emitted as <c>0</c> (YQL cannot parse an empty map).
    /// </summary>
    public YqlWhereClause Predicate(string field, IReadOnlyDictionary<string, string> attributes, IReadOnlyDictionary<string, long>? rangeAttributes = null)
    {
        YqlIdentifier.Validate(field, nameof(field));
        return AddPredicate(new YqlPredicate.Predicate(field, attributes, rangeAttributes));
    }

    internal YqlWhereClause AddPredicate(YqlPredicate predicate)
    {
        _andPredicates.Add(predicate);
        return this;
    }

    internal IReadOnlyList<YqlPredicate> GetPredicates() => _andPredicates;

    internal YqlPredicate BuildPredicate() =>
        _andPredicates.Count switch
        {
            0 => new YqlPredicate.True(),
            1 => _andPredicates[0],
            _ => new YqlPredicate.And(_andPredicates)
        };

    internal string Build() => BuildPredicate().Build();
}
