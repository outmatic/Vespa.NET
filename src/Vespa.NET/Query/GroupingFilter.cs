using System.Globalization;

namespace Vespa.Query;

/// <summary>
/// A grouping filter expression used inside <c>filter(...)</c> / <c>keep(...)</c>
/// clauses (docs.vespa.ai/en/reference/grouping-syntax.html, "Filtering groups").
/// Build atoms with <see cref="Regex"/>, <see cref="Range"/> and <see cref="IsTrue"/>,
/// then combine with <see cref="And"/>, <see cref="Or"/> and <see cref="Not(GroupingFilter)"/>.
/// </summary>
public abstract class GroupingFilter
{
    private protected GroupingFilter() { }

    /// <summary>
    /// <c>regex("pattern", expression)</c> — collect only values whose string
    /// representation matches the regular expression.
    /// The <paramref name="expression"/> (usually the same expression as in the
    /// <c>group()</c> clause) is embedded verbatim — do not pass unvalidated end-user input.
    /// </summary>
    public static GroupingFilter Regex(string pattern, string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return new RegexFilter(pattern, expression);
    }

    /// <summary>
    /// <c>range(min, max, expression[, minInclusive, maxInclusive])</c> — collect only
    /// values between the bounds. Defaults match Vespa: lower bound inclusive,
    /// upper bound exclusive; the boolean pair is emitted only for non-default bounds.
    /// The <paramref name="expression"/> is embedded verbatim.
    /// </summary>
    public static GroupingFilter Range(double min, double max, string expression, bool minInclusive = true, bool maxInclusive = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return new RangeFilter(min, max, expression, minInclusive, maxInclusive);
    }

    /// <summary>
    /// <c>istrue(expression)</c> — collect only documents where the boolean
    /// expression evaluates to true. The <paramref name="expression"/> is embedded verbatim.
    /// </summary>
    public static GroupingFilter IsTrue(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return new IsTrueFilter(expression);
    }

    /// <summary><c>not filter</c> — matches when the sub-filter does not.</summary>
    public static GroupingFilter Not(GroupingFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return new NotFilter(filter);
    }

    /// <summary><c>this and other</c> — matches only when both filters match.</summary>
    public GroupingFilter And(GroupingFilter other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new BinaryFilter(this, "and", other);
    }

    /// <summary><c>this or other</c> — matches when either filter matches.</summary>
    public GroupingFilter Or(GroupingFilter other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new BinaryFilter(this, "or", other);
    }

    internal abstract string Build();

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Parenthesize(GroupingFilter f) =>
        f is BinaryFilter ? $"({f.Build()})" : f.Build();

    private sealed class RegexFilter(string pattern, string expression) : GroupingFilter
    {
        internal override string Build() => $"regex(\"{Escape(pattern)}\", {expression})";
    }

    private sealed class RangeFilter(double min, double max, string expression, bool minInclusive, bool maxInclusive) : GroupingFilter
    {
        internal override string Build()
        {
            var inv = CultureInfo.InvariantCulture;
            var bounds = (minInclusive, maxInclusive) is (true, false)
                ? ""
                : $", {Bool(minInclusive)}, {Bool(maxInclusive)}";
            return $"range({min.ToString(inv)}, {max.ToString(inv)}, {expression}{bounds})";

            static string Bool(bool v) => v ? "true" : "false";
        }
    }

    private sealed class IsTrueFilter(string expression) : GroupingFilter
    {
        internal override string Build() => $"istrue({expression})";
    }

    private sealed class NotFilter(GroupingFilter inner) : GroupingFilter
    {
        internal override string Build() => $"not {Parenthesize(inner)}";
    }

    private sealed class BinaryFilter(GroupingFilter left, string op, GroupingFilter right) : GroupingFilter
    {
        internal override string Build() => $"{Parenthesize(left)} {op} {Parenthesize(right)}";
    }
}
