namespace Vespa.Query;

/// <summary>
/// Builder for conditions inside a <c>sameElement()</c> predicate.
/// </summary>
public sealed class SameElementClause
{
    private static readonly string[] AllowedOperators =
        ["contains", "=", ">", ">=", "<", "<=", "!=", "matches"];

    private readonly List<YqlPredicate> _predicates = [];

    /// <summary>
    /// Adds a <c>subField op value</c> condition. <paramref name="op"/> must be one of
    /// <c>contains</c>, <c>=</c>, <c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c>, <c>&lt;=</c>,
    /// <c>!=</c> or <c>matches</c>.
    /// </summary>
    public SameElementClause Field(string subField, string op, object value)
    {
        YqlIdentifier.Validate(subField, nameof(subField));
        if (!AllowedOperators.Contains(op))
            throw new ArgumentException(
                $"'{op}' is not a valid sameElement operator. Allowed: {string.Join(", ", AllowedOperators)}.",
                nameof(op));
        _predicates.Add(new YqlPredicate.Comparison(subField, op, value));
        return this;
    }

    public SameElementClause Contains(string subField, string value)
    {
        YqlIdentifier.Validate(subField, nameof(subField));
        _predicates.Add(new YqlPredicate.Contains(subField, value));
        return this;
    }

    /// <summary>
    /// Adds a bare term for array-of-string fields: <c>sameElement("term" and …)</c>.
    /// When bare terms are present, all conditions are combined with <c>and</c>.
    /// </summary>
    public SameElementClause Term(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _predicates.Add(new YqlPredicate.BareTerm(value));
        return this;
    }

    /// <summary>
    /// Adds a negated bare term for array-of-string fields:
    /// <c>sameElement("one" and !"two")</c> — the element must not contain the term.
    /// </summary>
    public SameElementClause NotTerm(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _predicates.Add(new YqlPredicate.BareTerm(value, negated: true));
        return this;
    }

    internal IReadOnlyList<YqlPredicate> GetPredicates() => _predicates;
}
