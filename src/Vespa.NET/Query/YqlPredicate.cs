using System.Globalization;

namespace Vespa.Query;

/// <summary>
/// Optional per-term annotations for <c>contains</c> predicates
/// (docs.vespa.ai/en/reference/query-language-reference.html, "Annotations").
/// </summary>
internal sealed record TermAnnotations(
    bool? Filter = null,
    int? TermId = null,
    double? ConnectivityWeight = null,
    bool? Stem = null,
    bool? NormalizeCase = null,
    bool? AccentDrop = null,
    bool? UsePositionData = null,
    int? Weight = null,
    bool? Prefix = null,
    bool? Suffix = null,
    bool? Substring = null,
    double? Significance = null,
    bool? Ranked = null,
    string? Language = null,
    bool? Nfkc = null,
    bool? ImplicitTransforms = null)
{
    public bool IsEmpty =>
        Filter is null && TermId is null && ConnectivityWeight is null && Stem is null &&
        NormalizeCase is null && AccentDrop is null && UsePositionData is null && Weight is null &&
        Prefix is null && Suffix is null && Substring is null && Significance is null &&
        Ranked is null && Language is null && Nfkc is null && ImplicitTransforms is null;
}

/// <summary>
/// Represents an atomic YQL predicate or a composite (AND/OR) expression
/// </summary>
internal abstract class YqlPredicate
{
    public abstract string Build();

    // --- Atomic predicates ---

    internal sealed class Contains(string field, string value, TermAnnotations? annotations = null) : YqlPredicate
    {
        public override string Build()
        {
            if (annotations is { IsEmpty: false })
            {
                var prefix = $"{{{string.Join(", ", BuildTermAnnotations(annotations))}}}";
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
        double? distanceThreshold = null, int? hnswExploreAdditionalHits = null,
        int? totalTargetHits = null, int? minTargetHits = null) : YqlPredicate
    {
        public override string Build()
        {
            // totalTargetHits is the documented, preferred annotation; when set it
            // replaces the per-node targetHits.
            var annotations = new List<string>
            {
                totalTargetHits.HasValue ? $"totalTargetHits:{totalTargetHits.Value}" : $"targetHits:{targetHits}"
            };
            if (minTargetHits.HasValue) annotations.Add($"minTargetHits:{minTargetHits.Value}");
            if (label is not null) annotations.Add($"label:\"{Escape(label)}\"");
            if (approximate.HasValue) annotations.Add($"approximate:{(approximate.Value ? "true" : "false")}");
            if (distanceThreshold.HasValue) annotations.Add($"distanceThreshold:{distanceThreshold.Value.ToString(CultureInfo.InvariantCulture)}");
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
    /// <c>field in (@param)</c> — parameter-substituted set membership; the values
    /// travel as a separate request parameter (recommended for large sets).
    /// </summary>
    internal sealed class InParameter(string field, string paramName) : YqlPredicate
    {
        public override string Build() => $"{field} in (@{paramName})";
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
            if (maxEditDistance.HasValue) opts.Append(System.Globalization.CultureInfo.InvariantCulture, $"maxEditDistance:{maxEditDistance}");
            if (prefixLength.HasValue)
            {
                if (opts.Length > 0) opts.Append(',');
                opts.Append(System.Globalization.CultureInfo.InvariantCulture, $"prefixLength:{prefixLength}");
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
            $"geoLocation({field}, {latitude.ToString(CultureInfo.InvariantCulture)}, {longitude.ToString(CultureInfo.InvariantCulture)}, \"{radiusKm.ToString(CultureInfo.InvariantCulture)}km\")";
    }

    /// <summary>
    /// <c>userQuery()</c> — parses free-text user input using Vespa's simple query language.
    /// The function takes no arguments: the text travels in the <c>model.queryString</c>
    /// request parameter (set automatically by <c>ToSearchRequest()</c>/<c>WithYql()</c>).
    /// </summary>
    internal sealed class UserQuery : YqlPredicate
    {
        public override string Build() => "userQuery()";
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

    /// <summary>
    /// <c>!(expr)</c> — logical negation
    /// (docs.vespa.ai/en/reference/query-language-reference.html, "not").
    /// </summary>
    internal sealed class Not(YqlPredicate inner) : YqlPredicate
    {
        public override string Build() => $"!({inner.Build()})";
    }

    /// <summary>
    /// Raw YQL fragment embedded verbatim — the caller is responsible for its validity.
    /// </summary>
    internal sealed class RawPredicate(string yql) : YqlPredicate
    {
        public override string Build() => yql;
    }

    // --- Proximity / synonym operators ---

    internal sealed class Near(string field, IReadOnlyList<string> terms, int? distance,
        IReadOnlyList<string>? excludedTerms = null, int? exclusionDistance = null, bool ordered = false) : YqlPredicate
    {
        public override string Build()
        {
            // Negative terms (!"term") must come after all positive terms
            var args = terms.Select(t => $"\"{Escape(t)}\"")
                .Concat((excludedTerms ?? []).Select(t => $"!\"{Escape(t)}\""));
            var name = ordered ? "onear" : "near";
            var call = $"{name}({string.Join(", ", args)})";

            var annotations = new List<string>();
            if (distance.HasValue) annotations.Add($"distance: {distance.Value}");
            if (exclusionDistance.HasValue) annotations.Add($"exclusionDistance: {exclusionDistance.Value}");
            var annotated = annotations.Count > 0 ? $"({{{string.Join(", ", annotations)}}}{call})" : call;
            return $"{field} contains {annotated}";
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

    /// <summary>
    /// A bare (optionally negated) term inside <c>sameElement()</c>, used for
    /// array-of-string fields: <c>sameElement("one" and !"two")</c>.
    /// </summary>
    internal sealed class BareTerm(string value, bool negated = false) : YqlPredicate
    {
        public override string Build() => $"{(negated ? "!" : "")}\"{Escape(value)}\"";
    }

    internal sealed class SameElement(string field, IReadOnlyList<YqlPredicate> conditions, IReadOnlyList<int>? elementFilter = null) : YqlPredicate
    {
        public override string Build()
        {
            // Bare-term conditions use the documented "a and !b" form; struct-field
            // conditions use the comma form (both combine with AND semantics).
            var separator = conditions.Any(p => p is BareTerm) ? " and " : ", ";
            var parts = string.Join(separator, conditions.Select(p => p.Build()));
            var call = $"sameElement({parts})";
            if (elementFilter is { Count: > 0 })
                return $"{field} contains ({{elementFilter:[{string.Join(", ", elementFilter)}]}}{call})";
            return $"{field} contains {call}";
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

    /// <summary>
    /// <c>geoBoundingBox(field, s, w, n, e)</c> — position inside a geographical
    /// rectangle; 5 positional arguments, south-western corner first
    /// (docs.vespa.ai/en/reference/query-language-reference.html, "geoBoundingBox").
    /// </summary>
    internal sealed class GeoBoundingBox(string field, double south, double west, double north, double east) : YqlPredicate
    {
        private static string Fmt(double v) => v.ToString(CultureInfo.InvariantCulture);
        public override string Build() =>
            $"geoBoundingBox({field}, {Fmt(south)}, {Fmt(west)}, {Fmt(north)}, {Fmt(east)})";
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
            // YQL cannot parse an empty map — the documented placeholder is the number 0
            var attrMap = attributes.Count > 0
                ? $"{{{string.Join(", ", attributes.Select(kv => $"\"{Escape(kv.Key)}\": \"{Escape(kv.Value)}\""))}}}"
                : "0";
            var rangeMap = rangeAttributes is { Count: > 0 }
                ? $"{{{string.Join(", ", rangeAttributes.Select(kv => $"\"{Escape(kv.Key)}\": {kv.Value}L"))}}}"
                : "0";
            return $"predicate({field}, {attrMap}, {rangeMap})";
        }
    }

    // --- Weak/weighted operators ---

    /// <summary>
    /// <c>[{targetHits:N}]weakAnd(pred1, pred2, ...)</c> — retrieves documents matching at least one
    /// sub-predicate and scores them by weakly combining all matches.
    /// </summary>
    internal sealed class WeakAnd(IReadOnlyList<YqlPredicate> operands, int? targetHits = null, int? totalTargetHits = null) : YqlPredicate
    {
        public override string Build()
        {
            var parts = string.Join(", ", operands.Select(p => p.Build()));
            var annotations = new List<string>();
            if (totalTargetHits.HasValue) annotations.Add($"totalTargetHits:{totalTargetHits.Value}");
            else if (targetHits.HasValue) annotations.Add($"targetHits:{targetHits.Value}");
            return annotations.Count > 0
                ? $"({{{string.Join(", ", annotations)}}}weakAnd({parts}))"
                : $"weakAnd({parts})";
        }
    }

    /// <summary>
    /// <c>{targetHits:N}wand(field, {{"term": weight, ...}})</c> — Weighted AND: returns
    /// the top-<paramref name="targetHits"/> documents by term-weight dot product.
    /// </summary>
    internal sealed class Wand(string field, IReadOnlyDictionary<string, int> terms, int targetHits, double? scoreThreshold = null, int? totalTargetHits = null) : YqlPredicate
    {
        public override string Build()
        {
            var entries = string.Join(", ", terms.Select(kv => $"\"{Escape(kv.Key)}\": {kv.Value}"));
            var annotations = new List<string>
            {
                totalTargetHits.HasValue ? $"totalTargetHits:{totalTargetHits.Value}" : $"targetHits:{targetHits}"
            };
            if (scoreThreshold.HasValue)
                annotations.Add($"scoreThreshold:{scoreThreshold.Value.ToString(CultureInfo.InvariantCulture)}");
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
    internal sealed class Text(string field, string value, string? language = null) : YqlPredicate
    {
        public override string Build()
        {
            var call = $"text(\"{Escape(value)}\")";
            return language is not null
                ? $"{field} contains ({{language: \"{Escape(language)}\"}}{call})"
                : $"{field} contains {call}";
        }
    }

    // --- Helpers ---

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string FormatValue(object value) => value switch
    {
        string s => $"\"{Escape(s)}\"",
        bool b => b ? "true" : "false",
        char c => $"\"{Escape(c.ToString())}\"",
        float f => f.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        // YQL requires the L suffix for 64-bit signed numbers
        long l => l.ToString(CultureInfo.InvariantCulture) + "L",
        ulong ul => ul <= long.MaxValue
            ? ul.ToString(CultureInfo.InvariantCulture) + "L"
            : throw new NotSupportedException(
                $"Value {ul} exceeds long.MaxValue — YQL numbers are signed 64-bit."),
        DateTime or DateTimeOffset => throw new NotSupportedException(
            "DateTime values cannot be expressed in YQL — convert to epoch milliseconds (long) to match the field's stored representation."),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException(
            $"Values of type {value.GetType()} cannot be expressed as YQL literals — use string, bool, or a numeric type.")
    };

    private static string Parenthesize(YqlPredicate p) => p is And or Or ? $"({p.Build()})" : p.Build();

    private static List<string> BuildTermAnnotations(TermAnnotations a)
    {
        var annotations = new List<string>();
        if (a.Filter.HasValue) annotations.Add($"filter: {Bool(a.Filter.Value)}");
        if (a.TermId.HasValue && a.ConnectivityWeight.HasValue)
            annotations.Add($"connectivity: {{id: {a.TermId.Value}, weight: {a.ConnectivityWeight.Value.ToString(CultureInfo.InvariantCulture)}}}");
        else if (a.TermId.HasValue)
            annotations.Add($"id: {a.TermId.Value}");
        if (a.Weight.HasValue) annotations.Add($"weight: {a.Weight.Value}");
        if (a.Stem.HasValue) annotations.Add($"stem: {Bool(a.Stem.Value)}");
        if (a.NormalizeCase.HasValue) annotations.Add($"normalizeCase: {Bool(a.NormalizeCase.Value)}");
        if (a.AccentDrop.HasValue) annotations.Add($"accentDrop: {Bool(a.AccentDrop.Value)}");
        if (a.UsePositionData.HasValue) annotations.Add($"usePositionData: {Bool(a.UsePositionData.Value)}");
        if (a.Prefix.HasValue) annotations.Add($"prefix: {Bool(a.Prefix.Value)}");
        if (a.Suffix.HasValue) annotations.Add($"suffix: {Bool(a.Suffix.Value)}");
        if (a.Substring.HasValue) annotations.Add($"substring: {Bool(a.Substring.Value)}");
        if (a.Significance.HasValue) annotations.Add($"significance: {a.Significance.Value.ToString(CultureInfo.InvariantCulture)}");
        if (a.Ranked.HasValue) annotations.Add($"ranked: {Bool(a.Ranked.Value)}");
        if (a.Language is not null) annotations.Add($"language: \"{Escape(a.Language)}\"");
        if (a.Nfkc.HasValue) annotations.Add($"nfkc: {Bool(a.Nfkc.Value)}");
        if (a.ImplicitTransforms.HasValue) annotations.Add($"implicitTransforms: {Bool(a.ImplicitTransforms.Value)}");
        return annotations;

        static string Bool(bool v) => v ? "true" : "false";
    }
}
