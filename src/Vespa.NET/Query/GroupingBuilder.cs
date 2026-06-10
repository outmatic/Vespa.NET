using System.Text;

namespace Vespa.Query;

/// <summary>
/// Fluent builder for a Vespa grouping expression appended to YQL with the pipe operator.
/// <para>
/// Example:
/// <code>
/// GroupingBuilder.All()
///     .Group("genre")
///     .Max(10)
///     .OrderByDescending(GroupingAgg.Count())
///     .Each(e => e.Output(GroupingAgg.Count(), GroupingAgg.Avg("year")))
/// // → all(group(genre) max(10) order(-count()) each(output(count() avg(year))))
/// </code>
/// </para>
/// </summary>
public sealed class GroupingBuilder
{
    private string? _groupField;
    private int? _max;
    private int? _precision;
    private string? _order;
    private bool _whereTrue;
    private readonly List<(string Name, string Expr)> _aliases = [];
    private EachGroupingBuilder? _each;
    private readonly bool _isAll;

    private GroupingBuilder(bool isAll) => _isAll = isAll;

    /// <summary>Creates a top-level <c>all(...)</c> grouping block</summary>
    public static GroupingBuilder All() => new(true);

    /// <summary>Creates a top-level <c>each(...)</c> grouping block (rarely needed at top level)</summary>
    public static GroupingBuilder Each() => new(false);

    /// <summary>Group by the specified field or expression</summary>
    public GroupingBuilder Group(string field)
    {
        _groupField = field;
        return this;
    }

    /// <summary>
    /// Group by fixed-width numeric buckets.
    /// Generates <c>group(fixedwidth(field, width))</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// .GroupByFixedWidth("price", 100)
    /// // → group(fixedwidth(price, 100))
    /// </code>
    /// </example>
    public GroupingBuilder GroupByFixedWidth(string field, double width)
    {
        _groupField = $"fixedwidth({field}, {width.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
        return this;
    }

    /// <summary>
    /// Group by predefined numeric buckets.
    /// Generates <c>group(predefined(field, bucket[from,to), ...))</c>.
    /// The last bucket implicitly extends to +∞.
    /// </summary>
    /// <example>
    /// <code>
    /// .GroupByBuckets("price", (0, 100), (100, 200), (200, 500))
    /// // → group(predefined(price, bucket[0,100), bucket[100,200), bucket[200,500)))
    /// </code>
    /// </example>
    public GroupingBuilder GroupByBuckets(string field, params (double From, double To)[] buckets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (buckets.Length == 0)
            throw new ArgumentException("At least one bucket is required.", nameof(buckets));

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var parts = string.Join(", ", buckets.Select(b => $"bucket[{b.From.ToString(inv)},{b.To.ToString(inv)})"));
        _groupField = $"predefined({field}, {parts})";
        return this;
    }

    /// <summary>
    /// Group by predefined string buckets (lexicographic ranges).
    /// Generates <c>group(predefined(field, bucket["from","to"), ...))</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// .GroupByBuckets("category", ("a", "m"), ("m", "z"))
    /// // → group(predefined(category, bucket["a","m"), bucket["m","z")))
    /// </code>
    /// </example>
    public GroupingBuilder GroupByBuckets(string field, params (string From, string To)[] buckets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (buckets.Length == 0)
            throw new ArgumentException("At least one bucket is required.", nameof(buckets));

        var parts = string.Join(", ", buckets.Select(b => $"""bucket["{Escape(b.From)}","{Escape(b.To)}")"""));
        _groupField = $"predefined({field}, {parts})";
        return this;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Maximum number of groups to return</summary>
    public GroupingBuilder Max(int n)
    {
        _max = n;
        return this;
    }

    /// <summary>Precision hint (oversamples groups before trimming to Max)</summary>
    public GroupingBuilder Precision(int n)
    {
        _precision = n;
        return this;
    }

    /// <summary>Order groups descending by the given expression (e.g. <see cref="GroupingAgg.Count"/>)</summary>
    public GroupingBuilder OrderByDescending(string expr)
    {
        _order = $"order(-{expr})";
        return this;
    }

    /// <summary>Order groups ascending by the given expression</summary>
    public GroupingBuilder OrderByAscending(string expr)
    {
        _order = $"order(+{expr})";
        return this;
    }

    /// <summary>
    /// Add <c>where(true)</c> to restrict grouping to only matching documents.
    /// </summary>
    public GroupingBuilder WhereTrue()
    {
        _whereTrue = true;
        return this;
    }

    /// <summary>
    /// Define a named alias for a grouping expression: <c>alias(name, expr)</c>.
    /// The alias can then be referenced as <c>$name</c> in output or order expressions.
    /// </summary>
    public GroupingBuilder Alias(string name, string expression)
    {
        _aliases.Add((name, expression));
        return this;
    }

    /// <summary>Configure what to output for each group</summary>
    public GroupingBuilder Each(Action<EachGroupingBuilder> configure)
    {
        _each = new EachGroupingBuilder();
        configure(_each);
        return this;
    }

    /// <summary>Build the grouping expression string (without the leading <c>|</c>)</summary>
    public string Build()
    {
        var sb = new StringBuilder(_isAll ? "all(" : "each(");

        if (_groupField is not null)
            sb.Append("group(").Append(_groupField).Append(") ");
        if (_max.HasValue)
            sb.Append("max(").Append(_max).Append(") ");
        if (_precision.HasValue)
            sb.Append("precision(").Append(_precision).Append(") ");
        if (_whereTrue)
            sb.Append("where(true) ");
        if (_order is not null)
            sb.Append(_order).Append(' ');
        foreach (var (name, expr) in _aliases)
            sb.Append("alias(").Append(name).Append(", ").Append(expr).Append(") ");
        if (_each is not null)
            sb.Append(_each.Build()).Append(' ');

        // Trim trailing space, close paren
        if (sb[^1] == ' ')
            sb.Length--;
        sb.Append(')');
        return sb.ToString();
    }

    public override string ToString() => Build();
}

/// <summary>
/// Configures the content of an <c>each(...)</c> block inside a <see cref="GroupingBuilder"/>
/// </summary>
public sealed class EachGroupingBuilder
{
    private readonly List<string> _outputs = [];
    private GroupingBuilder? _subGroup;
    private string? _summary;

    /// <summary>Add aggregation expressions to output for each group</summary>
    public EachGroupingBuilder Output(params string[] expressions)
    {
        _outputs.AddRange(expressions);
        return this;
    }

    /// <summary>
    /// Include document summaries of a specific summary class inside each group.
    /// Generates <c>summary(class)</c> or <c>summary()</c>.
    /// </summary>
    public EachGroupingBuilder Summary(string? summaryClass = null)
    {
        _summary = summaryClass is null ? "summary()" : $"summary({summaryClass})";
        return this;
    }

    /// <summary>Add a nested grouping inside each group (e.g. group by year within each genre)</summary>
    public EachGroupingBuilder SubGroup(GroupingBuilder nested)
    {
        _subGroup = nested;
        return this;
    }

    internal string Build()
    {
        var sb = new StringBuilder("each(");

        if (_outputs.Count > 0)
            sb.Append("output(").AppendJoin(", ", _outputs).Append(") ");
        if (_summary is not null)
            sb.Append(_summary).Append(' ');
        if (_subGroup is not null)
            sb.Append(_subGroup.Build()).Append(' ');

        if (sb[^1] == ' ')
            sb.Length--;
        sb.Append(')');
        return sb.ToString();
    }
}
