using System.Globalization;
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
    private bool _maxInf;
    private int? _precision;
    private readonly List<string> _orderKeys = [];
    private readonly List<(string Keyword, GroupingFilter Filter)> _filters = [];
    private readonly List<(string Name, string Expr)> _aliases = [];
    private readonly List<EachGroupingBuilder> _eaches = [];
    private string? _label;

    private GroupingBuilder() { }

    /// <summary>Creates a top-level <c>all(...)</c> grouping block</summary>
    public static GroupingBuilder All() => new();

    /// <summary>
    /// Group by the specified field or grouping expression.
    /// The value is embedded verbatim in the grouping clause — do not pass
    /// unvalidated end-user input here; use the bucket/fixed-width helpers for that.
    /// </summary>
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
        YqlIdentifier.Validate(field, nameof(field));
        _groupField = $"fixedwidth({field}, {width.ToString(CultureInfo.InvariantCulture)})";
        return this;
    }

    /// <summary>
    /// Group by predefined numeric buckets: <c>group(predefined(field, bucket[from,to), ...))</c>.
    /// Each bucket is inclusive of <c>From</c> and exclusive of <c>To</c>; values outside
    /// every bucket land in the "rest" group (<c>group:null</c>) — no implicit +∞ bucket
    /// is added. Use the <see cref="GroupByBuckets(string, GroupingBucket[])"/> overload
    /// for <c>inf</c> endpoints or open/closed bound variants.
    /// </summary>
    /// <example>
    /// <code>
    /// .GroupByBuckets("price", (0, 100), (100, 200), (200, 500))
    /// // → group(predefined(price, bucket[0,100), bucket[100,200), bucket[200,500)))
    /// </code>
    /// </example>
    public GroupingBuilder GroupByBuckets(string field, params (double From, double To)[] buckets)
        => GroupByBuckets(field, [.. buckets.Select(b => GroupingBucket.Numeric(b.From, b.To))]);

    /// <summary>
    /// Group by predefined string buckets (lexicographic ranges), inclusive of
    /// <c>From</c> and exclusive of <c>To</c>.
    /// Generates <c>group(predefined(field, bucket["from","to"), ...))</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// .GroupByBuckets("category", ("a", "m"), ("m", "z"))
    /// // → group(predefined(category, bucket["a","m"), bucket["m","z")))
    /// </code>
    /// </example>
    public GroupingBuilder GroupByBuckets(string field, params (string From, string To)[] buckets)
        => GroupByBuckets(field, [.. buckets.Select(b => GroupingBucket.Text(b.From, b.To))]);

    /// <summary>
    /// Group by predefined buckets with full control over bounds:
    /// open/closed endpoints and <c>-inf</c>/<c>inf</c> (see <see cref="GroupingBucket"/>).
    /// </summary>
    /// <example>
    /// <code>
    /// .GroupByBuckets("price",
    ///     GroupingBucket.Numeric(null, 100),          // bucket[-inf,100)
    ///     GroupingBucket.Numeric(100, 500, toInclusive: true),
    ///     GroupingBucket.Numeric(500, null))          // bucket[500,inf)
    /// </code>
    /// </example>
    public GroupingBuilder GroupByBuckets(string field, params GroupingBucket[] buckets)
    {
        YqlIdentifier.Validate(field, nameof(field));
        if (buckets.Length == 0)
            throw new ArgumentException("At least one bucket is required.", nameof(buckets));

        var parts = string.Join(", ", buckets.Select(b => b.Build()));
        _groupField = $"predefined({field}, {parts})";
        return this;
    }

    /// <summary>Maximum number of groups to return</summary>
    public GroupingBuilder Max(int n)
    {
        _max = n;
        _maxInf = false;
        return this;
    }

    /// <summary>
    /// <c>max(inf)</c> — return all groups (no group-count cap).
    /// </summary>
    public GroupingBuilder MaxAll()
    {
        _maxInf = true;
        _max = null;
        return this;
    }

    /// <summary>Precision hint (oversamples groups before trimming to Max)</summary>
    public GroupingBuilder Precision(int n)
    {
        _precision = n;
        return this;
    }

    /// <summary>
    /// Order groups descending by the given expression (e.g. <see cref="GroupingAgg.Count"/>).
    /// Call repeatedly for a multi-key order: <c>order(-count(), +avg(price))</c>.
    /// </summary>
    public GroupingBuilder OrderByDescending(string expr)
    {
        _orderKeys.Add($"-{expr}");
        return this;
    }

    /// <summary>
    /// Order groups ascending by the given expression.
    /// Call repeatedly for a multi-key order.
    /// </summary>
    public GroupingBuilder OrderByAscending(string expr)
    {
        _orderKeys.Add($"+{expr}");
        return this;
    }

    /// <summary>
    /// <c>filter(filterExp)</c> — restricts which values are collected into groups
    /// when grouping on multivalue attributes. Build the expression with
    /// <see cref="GroupingFilter"/> (<c>regex</c>, <c>range</c>, <c>istrue</c>, <c>not/and/or</c>).
    /// </summary>
    public GroupingBuilder Filter(GroupingFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _filters.Add(("filter", filter));
        return this;
    }

    /// <summary>
    /// <c>keep(filterExp)</c> — like <see cref="Filter"/> but keeps matching groups
    /// after they are formed instead of filtering the collected values.
    /// </summary>
    public GroupingBuilder Keep(GroupingFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _filters.Add(("keep", filter));
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

    /// <summary>
    /// Labels this grouping block: <c>all(...) as(label)</c>. The label replaces the
    /// auto-generated one in the response (<c>grouplist:label</c>). Only valid on
    /// nested blocks (the top-level request must be an unlabelled <c>all(...)</c>).
    /// </summary>
    public GroupingBuilder As(string label)
    {
        _label = YqlIdentifier.Validate(label, nameof(label));
        return this;
    }

    /// <summary>
    /// Configure what to output for each group. May be called multiple times to emit
    /// parallel <c>each(...)</c> blocks (label them with
    /// <see cref="EachGroupingBuilder.As"/> to tell the results apart).
    /// </summary>
    public GroupingBuilder Each(Action<EachGroupingBuilder> configure)
    {
        var each = new EachGroupingBuilder();
        configure(each);
        _eaches.Add(each);
        return this;
    }

    /// <summary>Build the grouping expression string (without the leading <c>|</c>)</summary>
    public string Build()
    {
        var sb = new StringBuilder("all(");

        if (_groupField is not null)
            sb.Append("group(").Append(_groupField).Append(") ");
        foreach (var (keyword, filter) in _filters)
            sb.Append(keyword).Append('(').Append(filter.Build()).Append(") ");
        if (_maxInf)
            sb.Append("max(inf) ");
        else if (_max.HasValue)
            sb.Append("max(").Append(_max).Append(") ");
        if (_precision.HasValue)
            sb.Append("precision(").Append(_precision).Append(") ");
        if (_orderKeys.Count > 0)
            sb.Append("order(").AppendJoin(", ", _orderKeys).Append(") ");
        foreach (var (name, expr) in _aliases)
            sb.Append("alias(").Append(name).Append(", ").Append(expr).Append(") ");
        foreach (var each in _eaches)
            sb.Append(each.Build()).Append(' ');

        // Trim trailing space, close paren
        if (sb[^1] == ' ')
            sb.Length--;
        sb.Append(')');
        if (_label is not null)
            sb.Append(" as(").Append(_label).Append(')');
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
    private readonly List<GroupingBuilder> _subGroups = [];
    private string? _summary;
    private string? _label;

    /// <summary>Add aggregation expressions to output for each group</summary>
    public EachGroupingBuilder Output(params string[] expressions)
    {
        _outputs.AddRange(expressions);
        return this;
    }

    /// <summary>
    /// Include document summaries for the hits inside each group.
    /// Generates the nested hit-list form Vespa requires:
    /// <c>max(N) each(output(summary(class)))</c>
    /// (see docs.vespa.ai/en/grouping.html). The returned documents appear as
    /// <c>VespaGroup.Hits</c> when parsed with <c>GroupByAsync</c>.
    /// </summary>
    /// <param name="summaryClass">Optional document-summary class; omit for the default summary.</param>
    /// <param name="maxHits">Optional maximum number of hits to return per group.</param>
    public EachGroupingBuilder Summary(string? summaryClass = null, int? maxHits = null)
    {
        var summary = summaryClass is null ? "summary()" : $"summary({summaryClass})";
        var prefix = maxHits.HasValue ? $"max({maxHits.Value}) " : "";
        _summary = $"{prefix}each(output({summary}))";
        return this;
    }

    /// <summary>
    /// Add a nested grouping inside each group (e.g. group by year within each genre).
    /// May be called multiple times for parallel sub-groupings; label them with
    /// <see cref="GroupingBuilder.As"/> to tell the result lists apart.
    /// </summary>
    public EachGroupingBuilder SubGroup(GroupingBuilder nested)
    {
        ArgumentNullException.ThrowIfNull(nested);
        _subGroups.Add(nested);
        return this;
    }

    /// <summary>
    /// Labels this <c>each</c> block: <c>each(...) as(label)</c>. Required to
    /// disambiguate parallel <c>each</c> blocks in the response.
    /// </summary>
    public EachGroupingBuilder As(string label)
    {
        _label = YqlIdentifier.Validate(label, nameof(label));
        return this;
    }

    internal string Build()
    {
        var sb = new StringBuilder("each(");

        if (_outputs.Count > 0)
            sb.Append("output(").AppendJoin(", ", _outputs).Append(") ");
        if (_summary is not null)
            sb.Append(_summary).Append(' ');
        foreach (var subGroup in _subGroups)
            sb.Append(subGroup.Build()).Append(' ');

        if (sb[^1] == ' ')
            sb.Length--;
        sb.Append(')');
        if (_label is not null)
            sb.Append(" as(").Append(_label).Append(')');
        return sb.ToString();
    }
}

/// <summary>
/// A single bucket for <c>predefined(...)</c> grouping, with control over bound
/// inclusivity and infinite endpoints
/// (grammar: <c>bucket[from,to)</c>, <c>bucket(from,to]</c>, <c>-inf</c>/<c>inf</c>).
/// </summary>
public sealed class GroupingBucket
{
    private readonly string _from;
    private readonly string _to;
    private readonly bool _fromInclusive;
    private readonly bool _toInclusive;

    private GroupingBucket(string from, string to, bool fromInclusive, bool toInclusive)
    {
        _from = from;
        _to = to;
        _fromInclusive = fromInclusive;
        _toInclusive = toInclusive;
    }

    /// <summary>
    /// Numeric bucket. Pass <see langword="null"/> for <paramref name="from"/> /
    /// <paramref name="to"/> to use <c>-inf</c> / <c>inf</c>. Defaults follow Vespa's
    /// canonical form: inclusive lower bound, exclusive upper bound.
    /// </summary>
    public static GroupingBucket Numeric(double? from, double? to, bool fromInclusive = true, bool toInclusive = false)
    {
        var inv = CultureInfo.InvariantCulture;
        return new GroupingBucket(
            from?.ToString(inv) ?? "-inf",
            to?.ToString(inv) ?? "inf",
            fromInclusive, toInclusive);
    }

    /// <summary>
    /// String (lexicographic) bucket. Pass <see langword="null"/> for
    /// <paramref name="from"/> / <paramref name="to"/> to use <c>-inf</c> / <c>inf</c>.
    /// </summary>
    public static GroupingBucket Text(string? from, string? to, bool fromInclusive = true, bool toInclusive = false)
        => new(
            from is null ? "-inf" : $"\"{Escape(from)}\"",
            to is null ? "inf" : $"\"{Escape(to)}\"",
            fromInclusive, toInclusive);

    internal string Build() =>
        $"bucket{(_fromInclusive ? '[' : '(')}{_from},{_to}{(_toInclusive ? ']' : ')')}";

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
