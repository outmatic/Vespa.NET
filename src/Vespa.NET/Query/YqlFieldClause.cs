namespace Vespa.Query;

/// <summary>
/// Intermediate builder returned by <see cref="YqlWhereClause.Field"/>
/// </summary>
public sealed class YqlFieldClause(string fieldName, YqlWhereClause parent)
{
    public YqlWhereClause Contains(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return parent.AddPredicate(new YqlPredicate.Contains(fieldName, value));
    }

    /// <summary>Matches a term with the specified Vespa annotations.</summary>
    /// <param name="value">The term to match.</param>
    /// <param name="annotations">The annotations to apply to this term.</param>
    public YqlWhereClause ContainsAnnotated(string value, YqlTermAnnotations annotations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentNullException.ThrowIfNull(annotations);
        return parent.AddPredicate(new YqlPredicate.Contains(fieldName, value, annotations));
    }

    /// <summary>
    /// <c>field contains text("value")</c> — linguistics-based text matching with tokenization and stemming.
    /// </summary>
    /// <param name="value">The text to match.</param>
    /// <param name="language">Optional RFC 3066 language code annotation (<c>{language: "…"}</c>).</param>
    public YqlWhereClause Text(string value, string? language = null)
        => parent.AddPredicate(new YqlPredicate.Text(fieldName, value, language));

    public YqlWhereClause Matches(string regex)
        => parent.AddPredicate(new YqlPredicate.Matches(fieldName, regex));

    public YqlWhereClause GreaterThan(object value)
        => parent.AddPredicate(new YqlPredicate.Comparison(fieldName, ">", value));

    public YqlWhereClause LessThan(object value)
        => parent.AddPredicate(new YqlPredicate.Comparison(fieldName, "<", value));

    public YqlWhereClause GreaterOrEqual(object value)
        => parent.AddPredicate(new YqlPredicate.Comparison(fieldName, ">=", value));

    public YqlWhereClause LessOrEqual(object value)
        => parent.AddPredicate(new YqlPredicate.Comparison(fieldName, "<=", value));

    public YqlWhereClause EqualTo(object value)
    {
        // YQL's = operator is defined for numeric and boolean values only;
        // field = "x" is a parse error on the server.
        if (value is string)
            throw new NotSupportedException(
                "YQL's = operator does not accept strings — use Contains(...) for text matching or In(...) for exact membership.");

        return parent.AddPredicate(new YqlPredicate.Comparison(fieldName, "=", value));
    }

    public YqlWhereClause Range(object from, object to, int? hitLimit = null, string? bounds = null)
        => parent.AddPredicate(new YqlPredicate.Range(fieldName, from, to, hitLimit, bounds));

    /// <summary>
    /// <c>field in ("a", "b")</c> — matches documents whose field value is one of the given values.
    /// <para>
    /// Supported for attribute or index fields of type <c>byte</c>, <c>int</c>, <c>long</c>
    /// or <c>string</c>. String fields must have <c>match: word</c> or <c>match: exact</c> —
    /// with the default <c>match: text</c> the field contents are tokenized at indexing time
    /// while <c>in</c> values are not, causing recall issues.
    /// </para>
    /// </summary>
    public YqlWhereClause In(params object[] values)
    {
        if (values.Length == 0)
            throw new ArgumentException("in requires at least one value — 'field in ()' is invalid YQL.", nameof(values));
        return parent.AddPredicate(new YqlPredicate.In(fieldName, values));
    }

    /// <summary>
    /// <c>field in (@param)</c> — set membership with parameter substitution.
    /// Pass the comma-separated values as a request parameter at query time
    /// (e.g. <c>request.WithParameter("param", "10,20,30")</c>). Recommended over
    /// inline values for large sets: faster query parsing, no YQL quoting.
    /// </summary>
    public YqlWhereClause InParameter(string paramName)
    {
        YqlIdentifier.Validate(paramName, nameof(paramName));
        return parent.AddPredicate(new YqlPredicate.InParameter(fieldName, paramName));
    }

    /// <summary>
    /// <c>field contains phrase("word1", "word2")</c> — ordered phrase match.
    /// </summary>
    public YqlWhereClause Phrase(params string[] terms)
        => parent.AddPredicate(new YqlPredicate.Phrase(fieldName, terms));

    /// <summary>
    /// <c>field contains fuzzy("term")</c> — approximate / fuzzy string match.
    /// </summary>
    /// <param name="term">The term to match approximately.</param>
    /// <param name="maxEditDistance">Max Levenshtein distance (default: 2).</param>
    /// <param name="prefixLength">Number of leading characters that must match exactly (default: 0).</param>
    public YqlWhereClause Fuzzy(string term, int? maxEditDistance = null, int? prefixLength = null)
        => parent.AddPredicate(new YqlPredicate.Fuzzy(fieldName, term, maxEditDistance, prefixLength));

    /// <summary>
    /// <c>field contains near("t1", "t2", !"t3")</c> — terms within <paramref name="distance"/>
    /// positions, any order. Terms in <paramref name="excludedTerms"/> are emitted negated
    /// (<c>!"term"</c>): documents match only when those terms do not occur within the
    /// exclusion zone (default <c>(distance+1)/2</c>, override with <paramref name="exclusionDistance"/>).
    /// </summary>
    public YqlWhereClause Near(string[] terms, int? distance = null, string[]? excludedTerms = null, int? exclusionDistance = null)
        => parent.AddPredicate(new YqlPredicate.Near(fieldName, terms, distance, excludedTerms, exclusionDistance));

    /// <summary>
    /// <c>field contains onear("t1", "t2", !"t3")</c> — ordered near; positive terms must
    /// appear in query sequence. See <see cref="Near"/> for the negated-term semantics.
    /// </summary>
    public YqlWhereClause ONear(string[] terms, int? distance = null, string[]? excludedTerms = null, int? exclusionDistance = null)
        => parent.AddPredicate(new YqlPredicate.Near(fieldName, terms, distance, excludedTerms, exclusionDistance, ordered: true));

    /// <summary>
    /// <c>field contains equiv("t1", "t2")</c> — treats terms as synonyms; ranks as a single term.
    /// </summary>
    public YqlWhereClause Equiv(params string[] terms)
        => parent.AddPredicate(new YqlPredicate.Equiv(fieldName, terms));

    /// <summary>
    /// <c>field contains sameElement(...)</c> — conditions must match within the same
    /// struct element or array element.
    /// </summary>
    /// <param name="configure">Builds the conditions that must match in the same element.</param>
    /// <param name="elementFilter">
    /// Optional element indices: <c>{elementFilter:[…]}sameElement(…)</c> restricts
    /// matching to those positions in the array field.
    /// </param>
    public YqlWhereClause SameElement(Action<SameElementClause> configure, int[]? elementFilter = null)
    {
        var clause = new SameElementClause();
        configure(clause);
        return parent.AddPredicate(new YqlPredicate.SameElement(fieldName, clause.GetPredicates(), elementFilter));
    }

    /// <summary>
    /// <c>field contains uri("value")</c> — URL component matching.
    /// </summary>
    [Obsolete("The Vespa uri field type (and the uri query operator) is deprecated upstream.")]
    public YqlWhereClause Uri(string value, bool? startAnchor = null, bool? endAnchor = null)
        => parent.AddPredicate(new YqlPredicate.Uri(fieldName, value, startAnchor, endAnchor));
}
