namespace Vespa.Query;

/// <summary>
/// Represents an atomic YQL predicate or a composite (AND/OR) expression
/// </summary>
internal abstract class YqlPredicate
{
    public abstract string Build();

    // --- Atomic predicates ---

    internal sealed class Contains(string field, string value, bool? filter = null, int? termId = null, double? connectivityWeight = null, bool? stem = null, bool? normalizeCase = null, bool? accentDrop = null, bool? usePositionData = null) : YqlPredicate
    {
        public override string Build()
        {
            var annotations = BuildTermAnnotations(filter, termId, connectivityWeight, stem, normalizeCase, accentDrop, usePositionData);
            if (annotations.Count > 0)
            {
                var prefix = $"{{{string.Join(", ", annotations)}}}";
                return $"{field} contains ({prefix}\"{Escape(value)}\")";
            }
            return $"""{field} contains "{Escape(value)}" """.TrimEnd();
        }
    }

    internal sealed class Matches(string field, string regex) : YqlPredicate
    {
        public override string Build() => $"""{field} matches "{Escape(regex)}" """.TrimEnd();
    }

    internal sealed class Comparison(string field, string op, object value) : YqlPredicate
    {
        public override string Build() => $"{field} {op} {FormatValue(value)}";
    }

    internal sealed class Range(string field, object from, object to, int? hitLimit = null, string? bounds = null) : YqlPredicate
    {
        public override string Build()
        {
            var annotations = new List<string>();
            if (hitLimit.HasValue) annotations.Add($"hitLimit:{hitLimit.Value}");
            if (bounds is not null) annotations.Add($"bounds:\"{Escape(bounds)}\"");
            var prefix = annotations.Count > 0 ? $"{{{string.Join(", ", annotations)}}}" : "";
            return $"{prefix}range({field}, {FormatValue(from)}, {FormatValue(to)})";
        }
    }

    internal sealed class NearestNeighbor(
        string field, string queryTensor, int targetHits,
        string? label = null, bool? approximate = null,
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null) : YqlPredicate
    {
        public override string Build()
        {
            var annotations = new List<string> { $"targetHits:{targetHits}" };
            if (label is not null) annotations.Add($"label:\"{Escape(label)}\"");
            if (approximate.HasValue) annotations.Add($"approximate:{(approximate.Value ? "true" : "false")}");
            if (distanceThreshold.HasValue) annotations.Add($"distanceThreshold:{distanceThreshold.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            if (hnswExploreAdditionalHits.HasValue) annotations.Add($"hnsw.exploreAdditionalHits:{hnswExploreAdditionalHits.Value}");
            return $"({{{string.Join(", ", annotations)}}}nearestNeighbor({field}, {queryTensor}))";
        }
    }

    /// <summary>
    /// <c>field in ("a", "b")</c> — matches any document whose field value is in the set.
    /// </summary>
    internal sealed class In(string field, IReadOnlyList<object> values) : YqlPredicate
    {
        public override string Build()
        {
            var list = string.Join(", ", values.Select(FormatValue));
            return $"{field} in ({list})";
        }
    }

    /// <summary>
    /// <c>field contains phrase("word1", "word2")</c> — ordered phrase match.
    /// </summary>
    internal sealed class Phrase(string field, IReadOnlyList<string> terms) : YqlPredicate
    {
        public override string Build()
        {
            var quoted = string.Join(", ", terms.Select(t => $"\"{Escape(t)}\""));
            return $"""{field} contains phrase({quoted})""";
        }
    }

    /// <summary>
    /// <c>field contains ([opts]fuzzy("term"))</c> — fuzzy / approximate string match.
    /// </summary>
    internal sealed class Fuzzy(string field, string term, int? maxEditDistance, int? prefixLength) : YqlPredicate
    {
        public override string Build()
        {
            var opts = new System.Text.StringBuilder();
            if (maxEditDistance.HasValue) opts.Append($"maxEditDistance:{maxEditDistance}");
            if (prefixLength.HasValue)
            {
                if (opts.Length > 0) opts.Append(',');
                opts.Append($"prefixLength:{prefixLength}");
            }
            var annotation = opts.Length > 0 ? $"({{{opts}}}fuzzy(\"{Escape(term)}\"))" : $"fuzzy(\"{Escape(term)}\")";
            return $"""{field} contains {annotation}""";
        }
    }

    /// <summary>
    /// <c>geoLocation(field, lat, lon, "Xkm")</c> — filters documents within the given radius.
    /// </summary>
    internal sealed class GeoLocation(string field, double latitude, double longitude, double radiusKm) : YqlPredicate
    {
        public override string Build() =>
            $"geoLocation({field}, {latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"{radiusKm.ToString(System.Globalization.CultureInfo.InvariantCulture)}km\")";
    }

    /// <summary>
    /// <c>userQuery("text")</c> — parses free-text user input using Vespa's default query language.
    /// Safe to use directly with end-user input (no injection risk).
    /// </summary>
    internal sealed class UserQuery(string query) : YqlPredicate
    {
        public override string Build() => $"userQuery(\"{Escape(query)}\")";
    }

    /// <summary>
    /// <c>userInput(@param)</c> — references a query parameter containing user input.
    /// The actual text is passed at query time via <c>VespaSearchRequest.Input</c>.
    /// </summary>
    internal sealed class UserInput(string paramName, string? grammar = null, string? defaultIndex = null, string? language = null, bool? allowEmpty = null) : YqlPredicate
    {
        public override string Build()
        {
            var annotations = new List<string>();
            if (grammar is not null) annotations.Add($"grammar: \"{Escape(grammar)}\"");
            if (defaultIndex is not null) annotations.Add($"defaultIndex: \"{Escape(defaultIndex)}\"");
            if (language is not null) annotations.Add($"language: \"{Escape(language)}\"");
            if (allowEmpty.HasValue) annotations.Add($"allowEmpty: {(allowEmpty.Value ? "true" : "false")}");
            var prefix = annotations.Count > 0 ? $"{{{string.Join(", ", annotations)}}}" : "";
            return $"{prefix}userInput(@{paramName})";
        }
    }

    internal sealed class True : YqlPredicate
    {
        public override string Build() => "true";
    }

    internal sealed class False : YqlPredicate
    {
        public override string Build() => "false";
    }

    // --- Proximity / synonym operators ---

    internal sealed class Near(string field, IReadOnlyList<string> terms, int? distance) : YqlPredicate
    {
        public override string Build()
        {
            var quoted = string.Join(", ", terms.Select(t => $"\"{Escape(t)}\""));
            var annotation = distance.HasValue ? $"({{distance: {distance.Value}}}near({quoted}))" : $"near({quoted})";
            return $"{field} contains {annotation}";
        }
    }

    internal sealed class ONear(string field, IReadOnlyList<string> terms, int? distance) : YqlPredicate
    {
        public override string Build()
        {
            var quoted = string.Join(", ", terms.Select(t => $"\"{Escape(t)}\""));
            var annotation = distance.HasValue ? $"({{distance: {distance.Value}}}onear({quoted}))" : $"onear({quoted})";
            return $"{field} contains {annotation}";
        }
    }

    internal sealed class Equiv(string field, IReadOnlyList<string> terms) : YqlPredicate
    {
        public override string Build()
        {
            var quoted = string.Join(", ", terms.Select(t => $"\"{Escape(t)}\""));
            return $"{field} contains equiv({quoted})";
        }
    }

    internal sealed class SameElement(string field, IReadOnlyList<YqlPredicate> conditions) : YqlPredicate
    {
        public override string Build()
        {
            var parts = string.Join(", ", conditions.Select(p => p.Build()));
            return $"{field} contains sameElement({parts})";
        }
    }

    internal sealed class WeightedSet(string field, IReadOnlyDictionary<string, int> tokens) : YqlPredicate
    {
        public override string Build()
        {
            var entries = string.Join(", ", tokens.Select(kv => $"\"{Escape(kv.Key)}\": {kv.Value}"));
            return $"weightedSet({field}, {{{entries}}})";
        }
    }

    internal sealed class Rank(IReadOnlyList<YqlPredicate> operands) : YqlPredicate
    {
        public override string Build()
        {
            var parts = string.Join(", ", operands.Select(p => p.Build()));
            return $"rank({parts})";
        }
    }

    /// <summary>
    /// <c>rank({targetHits:N}nearestNeighbor(field, tensor), userInput(@param))</c>
    /// — standard Vespa hybrid search: nearest-neighbor retrieves, text contributes to ranking.
    /// </summary>
    internal sealed class HybridSearch(YqlPredicate nearestNeighbor, UserInput userInput) : YqlPredicate
    {
        public override string Build() =>
            $"rank({nearestNeighbor.Build()}, {userInput.Build()})";
    }

    internal sealed class NonEmpty(YqlPredicate inner) : YqlPredicate
    {
        public override string Build() => $"nonEmpty({inner.Build()})";
    }

    internal sealed class GeoBoundingBox(string field, double south, double west, double north, double east) : YqlPredicate
    {
        private static string Fmt(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public override string Build() =>
            $"{{southWest: {{lat: {Fmt(south)}, lng: {Fmt(west)}}}, northEast: {{lat: {Fmt(north)}, lng: {Fmt(east)}}}}}geoBoundingBox({field})";
    }

    internal sealed class Uri(string field, string value, bool? startAnchor, bool? endAnchor) : YqlPredicate
    {
        public override string Build()
        {
            var annotations = new List<string>();
            if (startAnchor.HasValue) annotations.Add($"startAnchor: {(startAnchor.Value ? "true" : "false")}");
            if (endAnchor.HasValue) annotations.Add($"endAnchor: {(endAnchor.Value ? "true" : "false")}");
            var prefix = annotations.Count > 0 ? $"({{{string.Join(", ", annotations)}}}" : "(";
            return $"{field} contains {prefix}uri(\"{Escape(value)}\"))";
        }
    }

    internal sealed class Predicate(string field, IReadOnlyDictionary<string, string> attributes, IReadOnlyDictionary<string, long>? rangeAttributes) : YqlPredicate
    {
        public override string Build()
        {
            var attrEntries = string.Join(", ", attributes.Select(kv => $"\"{Escape(kv.Key)}\": \"{Escape(kv.Value)}\""));
            var attrMap = $"{{{attrEntries}}}";
            if (rangeAttributes is not null && rangeAttributes.Count > 0)
            {
                var rangeEntries = string.Join(", ", rangeAttributes.Select(kv => $"\"{Escape(kv.Key)}\": {kv.Value}L"));
                return $"predicate({field}, {attrMap}, {{{rangeEntries}}})";
            }
            return $"predicate({field}, {attrMap}, {{}})";
        }
    }

    // --- Weak/weighted operators ---

    /// <summary>
    /// <c>[{targetHits:N}]weakAnd(pred1, pred2, ...)</c> — retrieves documents matching at least one
    /// sub-predicate and scores them by weakly combining all matches.
    /// </summary>
    internal sealed class WeakAnd(IReadOnlyList<YqlPredicate> operands, int? targetHits = null) : YqlPredicate
    {
        public override string Build()
        {
            var parts = string.Join(", ", operands.Select(p => p.Build()));
            return targetHits.HasValue
                ? $"({{targetHits:{targetHits.Value}}}weakAnd({parts}))"
                : $"weakAnd({parts})";
        }
    }

    /// <summary>
    /// <c>{targetHits:N}wand(field, {{"term": weight, ...}})</c> — Weighted AND: returns
    /// the top-<paramref name="targetHits"/> documents by term-weight dot product.
    /// </summary>
    internal sealed class Wand(string field, IReadOnlyDictionary<string, int> terms, int targetHits, double? scoreThreshold = null) : YqlPredicate
    {
        public override string Build()
        {
            var entries = string.Join(", ", terms.Select(kv => $"\"{Escape(kv.Key)}\": {kv.Value}"));
            var annotations = new List<string> { $"targetHits:{targetHits}" };
            if (scoreThreshold.HasValue)
                annotations.Add($"scoreThreshold:{scoreThreshold.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return $"({{{string.Join(", ", annotations)}}}wand({field}, {{{entries}}}))";
        }
    }

    /// <summary>
    /// <c>dotProduct(field, {{"term": weight, ...}})</c> — scores documents by the
    /// dot product of the weighted term set against the field's term vector.
    /// </summary>
    internal sealed class DotProduct(string field, IReadOnlyDictionary<string, int> terms) : YqlPredicate
    {
        public override string Build()
        {
            var entries = string.Join(", ", terms.Select(kv => $"\"{Escape(kv.Key)}\": {kv.Value}"));
            return $"dotProduct({field}, {{{entries}}})";
        }
    }

    // --- Composite predicates ---

    internal sealed class And(IReadOnlyList<YqlPredicate> operands) : YqlPredicate
    {
        public override string Build()
        {
            var parts = operands.Select(p => Parenthesize(p)).ToList();
            return string.Join(" and ", parts);
        }
    }

    internal sealed class Or(IReadOnlyList<YqlPredicate> operands) : YqlPredicate
    {
        public override string Build()
        {
            var parts = operands.Select(p => Parenthesize(p)).ToList();
            return string.Join(" or ", parts);
        }
    }

    // --- Linguistics-based operator ---

    /// <summary>
    /// <c>field contains text("value")</c> — linguistics-based text matching with tokenization and stemming.
    /// </summary>
    internal sealed class Text(string field, string value) : YqlPredicate
    {
        public override string Build() => $"""{field} contains text("{Escape(value)}")""";
    }

    // --- Helpers ---

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string FormatValue(object value) => value switch
    {
        string s => $"\"{Escape(s)}\"",
        float f => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString()!
    };

    private static string Parenthesize(YqlPredicate p) => p is And or Or ? $"({p.Build()})" : p.Build();

    private static List<string> BuildTermAnnotations(
        bool? filter, int? termId, double? connectivityWeight,
        bool? stem, bool? normalizeCase, bool? accentDrop, bool? usePositionData)
    {
        var annotations = new List<string>();
        if (filter.HasValue) annotations.Add($"filter: {(filter.Value ? "true" : "false")}");
        if (termId.HasValue && connectivityWeight.HasValue)
            annotations.Add($"connectivity: {{id: {termId.Value}, weight: {connectivityWeight.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
        else if (termId.HasValue)
            annotations.Add($"id: {termId.Value}");
        if (stem.HasValue) annotations.Add($"stem: {(stem.Value ? "true" : "false")}");
        if (normalizeCase.HasValue) annotations.Add($"normalizeCase: {(normalizeCase.Value ? "true" : "false")}");
        if (accentDrop.HasValue) annotations.Add($"accentDrop: {(accentDrop.Value ? "true" : "false")}");
        if (usePositionData.HasValue) annotations.Add($"usePositionData: {(usePositionData.Value ? "true" : "false")}");
        return annotations;
    }
}
