namespace Vespa.Query;

/// <summary>
/// Factory for Vespa grouping output/aggregation expressions used inside output() and order()
/// </summary>
public static class GroupingAgg
{
    /// <summary>count() — number of documents in the group</summary>
    public static string Count() => "count()";

    /// <summary>sum(field) — sum of the field values</summary>
    public static string Sum(string field) => $"sum({field})";

    /// <summary>avg(field) — average of the field values</summary>
    public static string Avg(string field) => $"avg({field})";

    /// <summary>min(field) — minimum field value</summary>
    public static string Min(string field) => $"min({field})";

    /// <summary>max(field) — maximum field value</summary>
    public static string Max(string field) => $"max({field})";

    /// <summary>stddev(field) — standard deviation</summary>
    public static string StdDev(string field) => $"stddev({field})";

    /// <summary>xor(field) — XOR aggregation</summary>
    public static string Xor(string field) => $"xor({field})";

    /// <summary>
    /// quantiles([q1, q2, …], field) — quantile estimates of the field values in the group.
    /// </summary>
    /// <param name="quantiles">Quantile values between 0 and 1 inclusive (e.g. 0.95 for p95).</param>
    /// <param name="field">The numeric field to aggregate.</param>
    public static string Quantiles(IReadOnlyList<double> quantiles, string field)
    {
        ArgumentOutOfRangeException.ThrowIfZero(quantiles.Count, nameof(quantiles));
        foreach (var q in quantiles)
            if (q is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(quantiles), q, "Quantile values must be between 0 and 1 inclusive.");

        var list = string.Join(", ", quantiles.Select(q => q.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return $"quantiles([{list}], {field})";
    }

    /// <summary>
    /// relevance() — the average relevance score of documents in the group.
    /// Use inside <c>output()</c> to include the group's relevance in the response.
    /// </summary>
    public static string Relevance() => "relevance()";

    /// <summary>
    /// summary() / summary(name) — include document summaries for hits in the group.
    /// Use inside an <c>each()</c> block nested within a group to return the actual documents.
    /// </summary>
    /// <param name="summaryClass">
    /// Optional summary class name (as defined in the schema's <c>document-summary</c>).
    /// Omit to use the default summary.
    /// </param>
    public static string Summary(string? summaryClass = null) =>
        summaryClass is null ? "summary()" : $"summary({summaryClass})";

    // --- Time functions ---

    /// <summary>time.date(field) — date part of a timestamp (YYYY-MM-DD as integer)</summary>
    public static string TimeDate(string field) => $"time.date({field})";

    /// <summary>time.year(field) — year part of a timestamp</summary>
    public static string TimeYear(string field) => $"time.year({field})";

    /// <summary>time.monthofyear(field) — month of year (1-12)</summary>
    public static string TimeMonthOfYear(string field) => $"time.monthofyear({field})";

    /// <summary>time.dayofmonth(field) — day of month (1-31)</summary>
    public static string TimeDayOfMonth(string field) => $"time.dayofmonth({field})";

    /// <summary>time.hourofday(field) — hour of day (0-23)</summary>
    public static string TimeHourOfDay(string field) => $"time.hourofday({field})";

    /// <summary>time.minuteofhour(field) — minute of hour (0-59)</summary>
    public static string TimeMinuteOfHour(string field) => $"time.minuteofhour({field})";

    /// <summary>time.secondofminute(field) — second of minute (0-59)</summary>
    public static string TimeSecondOfMinute(string field) => $"time.secondofminute({field})";

    // --- Math functions ---

    /// <summary>math.floor(expr) — floor of the expression</summary>
    public static string MathFloor(string expr) => $"math.floor({expr})";

    /// <summary>math.log(expr) — natural logarithm</summary>
    public static string MathLog(string expr) => $"math.log({expr})";

    /// <summary>math.sqrt(expr) — square root</summary>
    public static string MathSqrt(string expr) => $"math.sqrt({expr})";

    /// <summary>math.abs(expr) — absolute value</summary>
    public static string MathAbs(string expr) => $"math.abs({expr})";

    /// <summary>math.pow(expr, n) — raise to a power</summary>
    public static string MathPow(string expr, double n) =>
        $"math.pow({expr}, {n.ToString(System.Globalization.CultureInfo.InvariantCulture)})";

    // --- Composite expressions ---

    /// <summary>cat(f1, f2) — string concatenation of two fields</summary>
    public static string Cat(string field1, string field2) => $"cat({field1}, {field2})";

    /// <summary>
    /// md5(field, maxLength, bits) — MD5 over the binary representation of the argument,
    /// keeping the lowest <paramref name="bits"/> bits.
    /// </summary>
    public static string Md5(string field, int maxLength, int bits) => $"md5({field}, {maxLength}, {bits})";

    /// <summary>
    /// uca(field, "locale"[, "strength"]) — Unicode Collation Algorithm sort key.
    /// </summary>
    /// <param name="field">The expression to collate.</param>
    /// <param name="locale">Locale string, e.g. <c>"sv"</c>.</param>
    /// <param name="strength">Optional strength: <c>"PRIMARY"</c>, <c>"SECONDARY"</c>, <c>"TERTIARY"</c>, <c>"QUATERNARY"</c> or <c>"IDENTICAL"</c>.</param>
    public static string Uca(string field, string locale, string? strength = null) =>
        strength is null ? $"uca({field}, \"{locale}\")" : $"uca({field}, \"{locale}\", \"{strength}\")";

    /// <summary>zcurve.x(field) — X coordinate from z-curve encoded value</summary>
    public static string ZCurveX(string field) => $"zcurve.x({field})";

    /// <summary>zcurve.y(field) — Y coordinate from z-curve encoded value</summary>
    public static string ZCurveY(string field) => $"zcurve.y({field})";

    /// <summary>docidnsspecific() — document ID namespace-specific part (streaming search)</summary>
    public static string DocIdNsSpecific() => "docidnsspecific()";

    // --- Aggregation math ---

    /// <summary>add(a, b) — sum of two aggregation expressions</summary>
    public static string Add(string a, string b) => $"add({a}, {b})";

    /// <summary>mul(a, b) — product of two aggregation expressions</summary>
    public static string Mul(string a, string b) => $"mul({a}, {b})";

    /// <summary>div(a, b) — division of two aggregation expressions</summary>
    public static string Div(string a, string b) => $"div({a}, {b})";

    /// <summary>mod(a, b) — modulo of two aggregation expressions</summary>
    public static string Mod(string a, string b) => $"mod({a}, {b})";

    /// <summary>neg(expr) — negation of an aggregation expression</summary>
    public static string Neg(string expr) => $"neg({expr})";

    /// <summary>cmp(f1, f2) — comparison of two expressions (-1, 0, or 1)</summary>
    public static string Cmp(string field1, string field2) => $"cmp({field1}, {field2})";

    /// <summary>fixedwidth(field, width) — fixed-width numeric bucket expression for use in group()</summary>
    public static string FixedWidth(string field, double width) =>
        $"fixedwidth({field}, {width.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
}
