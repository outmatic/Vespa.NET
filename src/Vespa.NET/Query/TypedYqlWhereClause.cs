using System.Linq.Expressions;
using Vespa.Models.Attributes;

namespace Vespa.Query;

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
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null,
        int? totalTargetHits = null, int? minTargetHits = null)
    {
        inner.NearestNeighbor(field, queryTensor, targetHits, label, approximate, distanceThreshold, hnswExploreAdditionalHits, totalTargetHits, minTargetHits);
        return this;
    }

    /// <summary>Nearest-neighbor with field name resolved via lambda.</summary>
    public TypedYqlWhereClause<T> NearestNeighbor(
        Expression<Func<T, object?>> fieldSelector,
        string queryTensor,
        int targetHits = 10,
        string? label = null, bool? approximate = null,
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null,
        int? totalTargetHits = null, int? minTargetHits = null)
    {
        inner.NearestNeighbor(fieldSelector, queryTensor, targetHits, label, approximate, distanceThreshold, hnswExploreAdditionalHits, totalTargetHits, minTargetHits);
        return this;
    }

    public TypedYqlWhereClause<T> And(Action<TypedYqlWhereClause<T>> subClause)
    {
        inner.And(Adapt(subClause));
        return this;
    }

    public TypedYqlWhereClause<T> Or(Action<TypedYqlWhereClause<T>> subClause)
    {
        inner.Or(Adapt(subClause));
        return this;
    }

    /// <summary>
    /// <c>!(expr)</c> — logical negation of the sub-expression.
    /// </summary>
    public TypedYqlWhereClause<T> Not(Action<TypedYqlWhereClause<T>> subClause)
    {
        inner.Not(Adapt(subClause));
        return this;
    }

    /// <inheritdoc cref="YqlWhereClause.Raw"/>
    public TypedYqlWhereClause<T> Raw(string yqlPredicate)
    {
        inner.Raw(yqlPredicate);
        return this;
    }

    /// <summary>
    /// <c>pred1 or pred2 or pred3</c> — matches documents satisfying any of the given predicates.
    /// </summary>
    public TypedYqlWhereClause<T> AnyOf(params Action<TypedYqlWhereClause<T>>[] predicates)
    {
        inner.AnyOf([.. predicates.Select(Adapt)]);
        return this;
    }

    public TypedYqlWhereClause<T> WeakAnd(Action<TypedYqlWhereClause<T>> configure, int? targetHits = null, int? totalTargetHits = null)
    {
        inner.WeakAnd(Adapt(configure), targetHits, totalTargetHits);
        return this;
    }

    public TypedYqlWhereClause<T> Wand(string field, IReadOnlyDictionary<string, int> terms, int targetHits = 100, double? scoreThreshold = null, int? totalTargetHits = null)
    {
        inner.Wand(field, terms, targetHits, scoreThreshold, totalTargetHits);
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
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null,
        int? totalTargetHits = null)
    {
        inner.HybridSearch(vectorField, queryTensor, userInputParam, targetHits, label, approximate, distanceThreshold, hnswExploreAdditionalHits, totalTargetHits);
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
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null,
        int? totalTargetHits = null)
    {
        inner.HybridSearch(VespaDocumentMeta.FieldName(vectorFieldSelector), queryTensor, userInputParam, targetHits, label, approximate, distanceThreshold, hnswExploreAdditionalHits, totalTargetHits);
        return this;
    }

    public TypedYqlWhereClause<T> Rank(params Action<TypedYqlWhereClause<T>>[] clauses)
    {
        inner.Rank([.. clauses.Select(Adapt)]);
        return this;
    }

    public TypedYqlWhereClause<T> NonEmpty(Action<TypedYqlWhereClause<T>> configure)
    {
        inner.NonEmpty(Adapt(configure));
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

    private static Action<YqlWhereClause> Adapt(Action<TypedYqlWhereClause<T>> configure) =>
        clause => configure(new(clause));
}
