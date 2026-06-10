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

    /// <summary>Logical AND with a sub-expression</summary>
    public YqlWhereClause And(Action<YqlWhereClause> subClause)
    {
        var sub = new YqlWhereClause();
        subClause(sub);
        _andPredicates.Add(sub.BuildPredicate());
        return this;
    }

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
        var built = predicates.Select(configure =>
        {
            var sub = new YqlWhereClause();
            configure(sub);
            return sub.BuildPredicate();
        }).ToList();
        _andPredicates.Add(new YqlPredicate.Or(built));
        return this;
    }

    /// <summary>
    /// <c>existing or (sub-predicates OR'd together)</c> — ORs existing predicates with the
    /// sub-expression. Multiple predicates in the callback are OR'd (not AND'd).
    /// </summary>
    public YqlWhereClause Or(Action<YqlWhereClause> subClause)
    {
        var sub = new YqlWhereClause();
        subClause(sub);
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
        _andPredicates.Add(allOperands.Count == 1 ? allOperands[0] : new YqlPredicate.Or(allOperands));
        return this;
    }

    /// <summary>Match all documents</summary>
    public YqlWhereClause True()
    {
        _andPredicates.Add(new YqlPredicate.True());
        return this;
    }

    /// <summary>Nearest-neighbor predicate with optional annotations</summary>
    public YqlWhereClause NearestNeighbor(
        string field, string queryTensor, int targetHits = 10,
        string? label = null, bool? approximate = null,
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null)
    {
        YqlIdentifier.Validate(field, nameof(field));
        YqlIdentifier.Validate(queryTensor, nameof(queryTensor));
        _andPredicates.Add(new YqlPredicate.NearestNeighbor(field, queryTensor, targetHits, label, approximate, distanceThreshold, hnswExploreAdditionalHits));
        return this;
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
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null) where T : class
        => NearestNeighbor(VespaDocumentMeta.FieldName(fieldSelector), queryTensor, targetHits, label, approximate, distanceThreshold, hnswExploreAdditionalHits);

    /// <summary>
    /// <c>[{targetHits:N}]weakAnd(pred1, pred2, ...)</c> — scores documents by weakly combining
    /// all matching sub-predicates; only the top hits are returned.
    /// </summary>
    /// <param name="configure">Callback that builds the sub-predicates.</param>
    /// <param name="targetHits">Optional optimizer hint controlling how many candidates weakAnd retrieves before ranking.</param>
    public YqlWhereClause WeakAnd(Action<YqlWhereClause> configure, int? targetHits = null)
    {
        var sub = new YqlWhereClause();
        configure(sub);
        var predicates = sub.GetPredicates();
        if (predicates.Count == 0)
            throw new ArgumentException("weakAnd requires at least one predicate.", nameof(configure));
        _andPredicates.Add(new YqlPredicate.WeakAnd(predicates, targetHits));
        return this;
    }

    /// <summary>
    /// <c>{targetHits:N}wand(field, {{"term": weight}})</c> — retrieves the top
    /// <paramref name="targetHits"/> documents by weighted term-set dot product.
    /// </summary>
    public YqlWhereClause Wand(string field, IReadOnlyDictionary<string, int> terms, int targetHits = 100, double? scoreThreshold = null)
    {
        _andPredicates.Add(new YqlPredicate.Wand(field, terms, targetHits, scoreThreshold));
        return this;
    }

    /// <summary>
    /// <c>dotProduct(field, {{"term": weight}})</c> — scores documents by the dot
    /// product of the weighted term set against the field's term vector.
    /// </summary>
    public YqlWhereClause DotProduct(string field, IReadOnlyDictionary<string, int> terms)
    {
        _andPredicates.Add(new YqlPredicate.DotProduct(field, terms));
        return this;
    }

    /// <summary>
    /// <c>geoLocation(field, lat, lon, "Xkm")</c> — filters documents within
    /// <paramref name="radiusKm"/> kilometres of the given coordinates.
    /// Requires a Vespa <c>position</c> field type.
    /// </summary>
    public YqlWhereClause GeoLocation(string field, double latitude, double longitude, double radiusKm)
    {
        _andPredicates.Add(new YqlPredicate.GeoLocation(field, latitude, longitude, radiusKm));
        return this;
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
        _andPredicates.Add(new YqlPredicate.UserQuery());
        return this;
    }

    /// <summary>Free text captured by <see cref="UserQuery"/>, sent as <c>model.queryString</c>.</summary>
    internal string? UserQueryText { get; private set; }

    /// <summary>
    /// <c>userInput(@param)</c> — references a query parameter containing user input.
    /// Pass the actual text via <c>VespaSearchRequest.Input["param"]</c> at query time.
    /// </summary>
    public YqlWhereClause UserInput(string paramName, string? grammar = null, string? defaultIndex = null, string? language = null, bool? allowEmpty = null)
    {
        _andPredicates.Add(new YqlPredicate.UserInput(paramName, grammar, defaultIndex, language, allowEmpty));
        return this;
    }

    /// <summary>
    /// Standard Vespa hybrid search pattern:
    /// <c>rank({targetHits:N}nearestNeighbor(field, tensor), userInput(@param))</c>.
    /// Nearest-neighbor retrieves documents; userInput contributes text-matching features for ranking.
    /// </summary>
    /// <param name="vectorField">The tensor field to search (e.g. "embedding").</param>
    /// <param name="queryTensor">The query tensor name (e.g. "q").</param>
    /// <param name="userInputParam">The userInput parameter name (without @).</param>
    /// <param name="targetHits">Target number of nearest-neighbor hits.</param>
    /// <param name="label">Optional label for the nearest-neighbor operator.</param>
    /// <param name="approximate">Whether to use approximate nearest-neighbor search.</param>
    /// <param name="distanceThreshold">Maximum distance threshold.</param>
    /// <param name="hnswExploreAdditionalHits">Extra HNSW exploration hits.</param>
    public YqlWhereClause HybridSearch(
        string vectorField, string queryTensor, string userInputParam,
        int targetHits = 100,
        string? label = null, bool? approximate = null,
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorField);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryTensor);
        ArgumentException.ThrowIfNullOrWhiteSpace(userInputParam);
        var nn = new YqlPredicate.NearestNeighbor(vectorField, queryTensor, targetHits, label, approximate, distanceThreshold, hnswExploreAdditionalHits);
        var ui = new YqlPredicate.UserInput(userInputParam);
        _andPredicates.Add(new YqlPredicate.HybridSearch(nn, ui));
        return this;
    }

    /// <summary>Match no documents</summary>
    public YqlWhereClause False()
    {
        _andPredicates.Add(new YqlPredicate.False());
        return this;
    }

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
        var predicates = clauses.Select(configure =>
        {
            var sub = new YqlWhereClause();
            configure(sub);
            return sub.BuildPredicate();
        }).ToList();
        _andPredicates.Add(new YqlPredicate.Rank(predicates));
        return this;
    }

    /// <summary>
    /// <c>nonEmpty(expr)</c> — validates that the sub-expression is not empty after
    /// parameter substitution.
    /// </summary>
    public YqlWhereClause NonEmpty(Action<YqlWhereClause> configure)
    {
        var sub = new YqlWhereClause();
        configure(sub);
        _andPredicates.Add(new YqlPredicate.NonEmpty(sub.BuildPredicate()));
        return this;
    }

    /// <summary>
    /// <c>{southWest:{lat:S,lng:W},northEast:{lat:N,lng:E}}geoBoundingBox(field)</c>
    /// </summary>
    public YqlWhereClause GeoBoundingBox(string field, double south, double west, double north, double east)
    {
        _andPredicates.Add(new YqlPredicate.GeoBoundingBox(field, south, west, north, east));
        return this;
    }

    /// <summary>
    /// <c>weightedSet(field, {{"token": weight}})</c> — matches documents containing
    /// any of the tokens, scoring by the largest matched weight.
    /// </summary>
    public YqlWhereClause WeightedSet(string field, IReadOnlyDictionary<string, int> tokens)
    {
        _andPredicates.Add(new YqlPredicate.WeightedSet(field, tokens));
        return this;
    }

    /// <summary>
    /// <c>predicate(field, attributes, ranges)</c> — queries a predicate field.
    /// </summary>
    public YqlWhereClause Predicate(string field, IReadOnlyDictionary<string, string> attributes, IReadOnlyDictionary<string, long>? rangeAttributes = null)
    {
        _andPredicates.Add(new YqlPredicate.Predicate(field, attributes, rangeAttributes));
        return this;
    }

    internal void AddPredicate(YqlPredicate predicate) => _andPredicates.Add(predicate);
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

/// <summary>
/// Intermediate builder returned by <see cref="YqlWhereClause.Field"/>
/// </summary>
public sealed class YqlFieldClause(string fieldName, YqlWhereClause parent)
{
    public YqlWhereClause Contains(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        parent.AddPredicate(new YqlPredicate.Contains(fieldName, value));
        return parent;
    }

    /// <summary>
    /// <c>field contains ({annotations}"value")</c> — contains with term annotations.
    /// </summary>
    /// <param name="value">The term to match.</param>
    /// <param name="filter">Mark as non-ranking filter term.</param>
    /// <param name="termId">Term ID for connectivity linking.</param>
    /// <param name="connectivityWeight">Weight for connectivity to the term identified by <paramref name="termId"/>.</param>
    /// <param name="stem">Override stemming for this term.</param>
    /// <param name="normalizeCase">Override case normalization for this term.</param>
    /// <param name="accentDrop">Override accent removal for this term.</param>
    /// <param name="usePositionData">Override position data usage for this term.</param>
    public YqlWhereClause ContainsAnnotated(string value,
        bool? filter = null, int? termId = null, double? connectivityWeight = null,
        bool? stem = null, bool? normalizeCase = null, bool? accentDrop = null, bool? usePositionData = null)
    {
        parent.AddPredicate(new YqlPredicate.Contains(fieldName, value, filter, termId, connectivityWeight, stem, normalizeCase, accentDrop, usePositionData));
        return parent;
    }

    /// <summary>
    /// <c>field contains text("value")</c> — linguistics-based text matching with tokenization and stemming.
    /// </summary>
    public YqlWhereClause Text(string value)
    {
        parent.AddPredicate(new YqlPredicate.Text(fieldName, value));
        return parent;
    }

    public YqlWhereClause Matches(string regex)
    {
        parent.AddPredicate(new YqlPredicate.Matches(fieldName, regex));
        return parent;
    }

    public YqlWhereClause GreaterThan(object value)
    {
        parent.AddPredicate(new YqlPredicate.Comparison(fieldName, ">", value));
        return parent;
    }

    public YqlWhereClause LessThan(object value)
    {
        parent.AddPredicate(new YqlPredicate.Comparison(fieldName, "<", value));
        return parent;
    }

    public YqlWhereClause GreaterOrEqual(object value)
    {
        parent.AddPredicate(new YqlPredicate.Comparison(fieldName, ">=", value));
        return parent;
    }

    public YqlWhereClause LessOrEqual(object value)
    {
        parent.AddPredicate(new YqlPredicate.Comparison(fieldName, "<=", value));
        return parent;
    }

    public YqlWhereClause EqualTo(object value)
    {
        // YQL's = operator is defined for numeric and boolean values only;
        // field = "x" is a parse error on the server.
        if (value is string)
            throw new NotSupportedException(
                "YQL's = operator does not accept strings — use Contains(...) for text matching or In(...) for exact membership.");

        parent.AddPredicate(new YqlPredicate.Comparison(fieldName, "=", value));
        return parent;
    }

    public YqlWhereClause Range(object from, object to, int? hitLimit = null, string? bounds = null)
    {
        parent.AddPredicate(new YqlPredicate.Range(fieldName, from, to, hitLimit, bounds));
        return parent;
    }

    /// <summary>
    /// <c>field in ("a", "b")</c> — matches documents whose field value is one of the given values.
    /// <para>
    /// <b>Important:</b> The field must have <c>indexing: index</c> (or <c>index | attribute | summary</c>).
    /// Attribute-only fields do not support <c>in</c> and will result in a <c>400 Bad Request</c> from Vespa.
    /// </para>
    /// </summary>
    public YqlWhereClause In(params object[] values)
    {
        parent.AddPredicate(new YqlPredicate.In(fieldName, values));
        return parent;
    }

    /// <summary>
    /// <c>field contains phrase("word1", "word2")</c> — ordered phrase match.
    /// </summary>
    public YqlWhereClause Phrase(params string[] terms)
    {
        parent.AddPredicate(new YqlPredicate.Phrase(fieldName, terms));
        return parent;
    }

    /// <summary>
    /// <c>field contains fuzzy("term")</c> — approximate / fuzzy string match.
    /// </summary>
    /// <param name="term">The term to match approximately.</param>
    /// <param name="maxEditDistance">Max Levenshtein distance (default: 2).</param>
    /// <param name="prefixLength">Number of leading characters that must match exactly (default: 0).</param>
    public YqlWhereClause Fuzzy(string term, int? maxEditDistance = null, int? prefixLength = null)
    {
        parent.AddPredicate(new YqlPredicate.Fuzzy(fieldName, term, maxEditDistance, prefixLength));
        return parent;
    }

    /// <summary>
    /// <c>field contains near("t1", "t2")</c> — terms within <paramref name="distance"/> positions, any order.
    /// </summary>
    public YqlWhereClause Near(string[] terms, int? distance = null)
    {
        parent.AddPredicate(new YqlPredicate.Near(fieldName, terms, distance));
        return parent;
    }

    /// <summary>
    /// <c>field contains onear("t1", "t2")</c> — ordered near; terms must appear in query sequence.
    /// </summary>
    public YqlWhereClause ONear(string[] terms, int? distance = null)
    {
        parent.AddPredicate(new YqlPredicate.ONear(fieldName, terms, distance));
        return parent;
    }

    /// <summary>
    /// <c>field contains equiv("t1", "t2")</c> — treats terms as synonyms; ranks as a single term.
    /// </summary>
    public YqlWhereClause Equiv(params string[] terms)
    {
        parent.AddPredicate(new YqlPredicate.Equiv(fieldName, terms));
        return parent;
    }

    /// <summary>
    /// <c>field contains sameElement(...)</c> — conditions must match within the same
    /// struct element or array element.
    /// </summary>
    public YqlWhereClause SameElement(Action<SameElementClause> configure)
    {
        var clause = new SameElementClause();
        configure(clause);
        parent.AddPredicate(new YqlPredicate.SameElement(fieldName, clause.GetPredicates()));
        return parent;
    }

    /// <summary>
    /// <c>field contains uri("value")</c> — URL component matching.
    /// </summary>
    public YqlWhereClause Uri(string value, bool? startAnchor = null, bool? endAnchor = null)
    {
        parent.AddPredicate(new YqlPredicate.Uri(fieldName, value, startAnchor, endAnchor));
        return parent;
    }
}

/// <summary>
/// Builder for conditions inside a <c>sameElement()</c> predicate.
/// </summary>
public sealed class SameElementClause
{
    private readonly List<YqlPredicate> _predicates = [];

    public SameElementClause Field(string subField, string op, object value)
    {
        _predicates.Add(new YqlPredicate.Comparison(subField, op, value));
        return this;
    }

    public SameElementClause Contains(string subField, string value)
    {
        _predicates.Add(new YqlPredicate.Contains(subField, value));
        return this;
    }

    internal IReadOnlyList<YqlPredicate> GetPredicates() => _predicates;
}

/// <summary>
/// Type-aware wrapper over <see cref="YqlWhereClause"/> surfaced by <see cref="YqlBuilder{T}.Where"/>.
/// Allows <c>Field(p => p.MyProp)</c> without repeating the document type argument.
/// </summary>
public sealed class TypedYqlWhereClause<T>(YqlWhereClause inner) where T : class
{
    /// <summary>
    /// Start a predicate whose field name is resolved from <c>[VespaField]</c> on the
    /// selected property (falls back to the property name).
    /// </summary>
    public YqlFieldClause Field(Expression<Func<T, object?>> selector)
        => inner.Field(selector);

    /// <summary>Start a predicate on the named field.</summary>
    public YqlFieldClause Field(string fieldName) => inner.Field(fieldName);

    public TypedYqlWhereClause<T> True() { inner.True(); return this; }

    public TypedYqlWhereClause<T> NearestNeighbor(
        string field, string queryTensor, int targetHits = 10,
        string? label = null, bool? approximate = null,
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null)
    {
        inner.NearestNeighbor(field, queryTensor, targetHits, label, approximate, distanceThreshold, hnswExploreAdditionalHits);
        return this;
    }

    /// <summary>Nearest-neighbor with field name resolved via lambda.</summary>
    public TypedYqlWhereClause<T> NearestNeighbor(
        Expression<Func<T, object?>> fieldSelector,
        string queryTensor,
        int targetHits = 10,
        string? label = null, bool? approximate = null,
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null)
    {
        inner.NearestNeighbor(fieldSelector, queryTensor, targetHits, label, approximate, distanceThreshold, hnswExploreAdditionalHits);
        return this;
    }

    public TypedYqlWhereClause<T> And(Action<TypedYqlWhereClause<T>> subClause)
    {
        inner.And(w => subClause(new TypedYqlWhereClause<T>(w)));
        return this;
    }

    public TypedYqlWhereClause<T> Or(Action<TypedYqlWhereClause<T>> subClause)
    {
        inner.Or(w => subClause(new TypedYqlWhereClause<T>(w)));
        return this;
    }

    /// <summary>
    /// <c>pred1 or pred2 or pred3</c> — matches documents satisfying any of the given predicates.
    /// </summary>
    public TypedYqlWhereClause<T> AnyOf(params Action<TypedYqlWhereClause<T>>[] predicates)
    {
        inner.AnyOf([.. predicates.Select<Action<TypedYqlWhereClause<T>>, Action<YqlWhereClause>>(c => w => c(new TypedYqlWhereClause<T>(w)))]);
        return this;
    }

    public TypedYqlWhereClause<T> WeakAnd(Action<TypedYqlWhereClause<T>> configure, int? targetHits = null)
    {
        inner.WeakAnd(w => configure(new TypedYqlWhereClause<T>(w)), targetHits);
        return this;
    }

    public TypedYqlWhereClause<T> Wand(string field, IReadOnlyDictionary<string, int> terms, int targetHits = 100, double? scoreThreshold = null)
    {
        inner.Wand(field, terms, targetHits, scoreThreshold);
        return this;
    }

    public TypedYqlWhereClause<T> DotProduct(string field, IReadOnlyDictionary<string, int> terms)
    {
        inner.DotProduct(field, terms);
        return this;
    }

    public TypedYqlWhereClause<T> GeoLocation(string field, double latitude, double longitude, double radiusKm)
    {
        inner.GeoLocation(field, latitude, longitude, radiusKm);
        return this;
    }

    public TypedYqlWhereClause<T> GeoLocation(
        Expression<Func<T, object?>> fieldSelector,
        double latitude, double longitude, double radiusKm)
    {
        inner.GeoLocation(VespaDocumentMeta.FieldName(fieldSelector), latitude, longitude, radiusKm);
        return this;
    }

    public TypedYqlWhereClause<T> UserQuery(string query)
    {
        inner.UserQuery(query);
        return this;
    }

    public TypedYqlWhereClause<T> UserInput(string paramName, string? grammar = null, string? defaultIndex = null, string? language = null, bool? allowEmpty = null)
    {
        inner.UserInput(paramName, grammar, defaultIndex, language, allowEmpty);
        return this;
    }

    public TypedYqlWhereClause<T> False() { inner.False(); return this; }

    /// <summary>
    /// Standard Vespa hybrid search: <c>rank(nearestNeighbor(...), userInput(@param))</c>.
    /// </summary>
    public TypedYqlWhereClause<T> HybridSearch(
        string vectorField, string queryTensor, string userInputParam,
        int targetHits = 100,
        string? label = null, bool? approximate = null,
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null)
    {
        inner.HybridSearch(vectorField, queryTensor, userInputParam, targetHits, label, approximate, distanceThreshold, hnswExploreAdditionalHits);
        return this;
    }

    /// <summary>
    /// Standard Vespa hybrid search with field name resolved via lambda.
    /// </summary>
    public TypedYqlWhereClause<T> HybridSearch(
        Expression<Func<T, object?>> vectorFieldSelector,
        string queryTensor, string userInputParam,
        int targetHits = 100,
        string? label = null, bool? approximate = null,
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null)
    {
        inner.HybridSearch(VespaDocumentMeta.FieldName(vectorFieldSelector), queryTensor, userInputParam, targetHits, label, approximate, distanceThreshold, hnswExploreAdditionalHits);
        return this;
    }

    public TypedYqlWhereClause<T> Rank(params Action<TypedYqlWhereClause<T>>[] clauses)
    {
        inner.Rank([.. clauses.Select<Action<TypedYqlWhereClause<T>>, Action<YqlWhereClause>>(c => w => c(new TypedYqlWhereClause<T>(w)))]);
        return this;
    }

    public TypedYqlWhereClause<T> NonEmpty(Action<TypedYqlWhereClause<T>> configure)
    {
        inner.NonEmpty(w => configure(new TypedYqlWhereClause<T>(w)));
        return this;
    }

    public TypedYqlWhereClause<T> GeoBoundingBox(string field, double south, double west, double north, double east)
    {
        inner.GeoBoundingBox(field, south, west, north, east);
        return this;
    }

    public TypedYqlWhereClause<T> GeoBoundingBox(
        Expression<Func<T, object?>> fieldSelector,
        double south, double west, double north, double east)
    {
        inner.GeoBoundingBox(VespaDocumentMeta.FieldName(fieldSelector), south, west, north, east);
        return this;
    }

    public TypedYqlWhereClause<T> WeightedSet(string field, IReadOnlyDictionary<string, int> tokens)
    {
        inner.WeightedSet(field, tokens);
        return this;
    }

    public TypedYqlWhereClause<T> Predicate(string field, IReadOnlyDictionary<string, string> attributes, IReadOnlyDictionary<string, long>? rangeAttributes = null)
    {
        inner.Predicate(field, attributes, rangeAttributes);
        return this;
    }
}
