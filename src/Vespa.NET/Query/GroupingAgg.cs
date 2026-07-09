namespace Vespa.Query;

/// <summary>
/// Factory for Vespa grouping output/aggregation expressions used inside output() and order()
/// </summary>
public static class GroupingAgg
{
    /// <summary>count() — number of documents in the group</summary>
    public static string Count() => "count()";

    /// <summary>
    /// expr as(label) — labels an aggregator inside <c>output(...)</c>; the label
    /// replaces the expression string as the aggregation key in the response.
    /// </summary>
    /// <example><code>e.Output(GroupingAgg.As(GroupingAgg.Count(), "n"))  // count() as(n)</code></example>
    public static string As(string expression, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        YqlIdentifier.Validate(label, nameof(label));
        return $"{expression} as({label})";
    }

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

    /// <summary>time.dayofweek(field) — day of week (0-6), Monday being 0</summary>
    public static string TimeDayOfWeek(string field) => $"time.dayofweek({field})";

    /// <summary>time.dayofyear(field) — day of year (0-365)</summary>
    public static string TimeDayOfYear(string field) => $"time.dayofyear({field})";

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
    // The grouping grammar has no math.floor/math.abs/cmp — only the functions below.

    /// <summary>math.log(expr) — natural logarithm</summary>
    public static string MathLog(string expr) => $"math.log({expr})";

    /// <summary>math.sqrt(expr) — square root</summary>
    public static string MathSqrt(string expr) => $"math.sqrt({expr})";

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

    // --- String / conversion functions ---

    /// <summary>strlen(expr) — number of bytes in the string</summary>
    public static string StrLen(string expr) => $"strlen({expr})";

    /// <summary>strcat(e1, e2, …) — string concatenation</summary>
    public static string StrCat(params string[] expressions)
    {
        ArgumentOutOfRangeException.ThrowIfZero(expressions.Length, nameof(expressions));
        return $"strcat({string.Join(", ", expressions)})";
    }

    /// <summary>tostring(expr) — convert to string</summary>
    public static string ToStringValue(string expr) => $"tostring({expr})";

    /// <summary>tolong(expr) — convert to integer</summary>
    public static string ToLong(string expr) => $"tolong({expr})";

    /// <summary>todouble(expr) — convert to double</summary>
    public static string ToDouble(string expr) => $"todouble({expr})";

    /// <summary>toraw(expr) — convert to raw bytes</summary>
    public static string ToRaw(string expr) => $"toraw({expr})";

    // --- List functions ---

    /// <summary>size(expr) — number of elements (1 for scalars)</summary>
    public static string Size(string expr) => $"size({expr})";

    /// <summary>sort(expr) — sort the elements of a multivalue expression ascending</summary>
    public static string Sort(string expr) => $"sort({expr})";

    /// <summary>reverse(expr) — reverse the elements of a multivalue expression</summary>
    public static string Reverse(string expr) => $"reverse({expr})";

    // --- Accessor / lookup functions ---

    /// <summary>attribute.at(index) — array element access on an attribute</summary>
    public static string At(string attributeName, int index)
    {
        YqlIdentifier.Validate(attributeName, nameof(attributeName));
        return $"{attributeName}.at({index})";
    }

    /// <summary>
    /// interpolatedlookup(attribute, expr) — index of the first element ≥ the lookup
    /// value in a sorted numeric array, with linear interpolation between elements.
    /// </summary>
    public static string InterpolatedLookup(string attributeName, string lookupExpression)
    {
        YqlIdentifier.Validate(attributeName, nameof(attributeName));
        return $"interpolatedlookup({attributeName}, {lookupExpression})";
    }

    /// <summary>
    /// geo_distance(attribute(field), lat, lon).km|miles — distance from a position
    /// attribute to the given coordinates, e.g. for fixed-width distance bucketing.
    /// </summary>
    /// <param name="attributeName">The position attribute name.</param>
    /// <param name="latitude">Latitude in degrees.</param>
    /// <param name="longitude">Longitude in degrees.</param>
    /// <param name="miles">Return miles instead of kilometres.</param>
    public static string GeoDistance(string attributeName, double latitude, double longitude, bool miles = false)
    {
        YqlIdentifier.Validate(attributeName, nameof(attributeName));
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return $"geo_distance(attribute({attributeName}), {latitude.ToString(inv)}, {longitude.ToString(inv)}).{(miles ? "miles" : "km")}";
    }

    // --- Aggregation math ---

    /// <summary>add(a, b) — sum of two aggregation expressions</summary>
    public static string Add(string a, string b) => $"add({a}, {b})";

    /// <summary>mul(a, b) — product of two aggregation expressions</summary>
    public static string Mul(string a, string b) => $"mul({a}, {b})";

    /// <summary>div(a, b) — division of two aggregation expressions</summary>
    public static string Div(string a, string b) => $"div({a}, {b})";

    /// <summary>mod(a, b) — modulo of two aggregation expressions</summary>
    public static string Mod(string a, string b) => $"mod({a}, {b})";

    /// <summary>sub(a, b) — difference of two aggregation expressions</summary>
    public static string Sub(string a, string b) => $"sub({a}, {b})";

    /// <summary>neg(expr) — negation of an aggregation expression</summary>
    public static string Neg(string expr) => $"neg({expr})";

    /// <summary>
    /// xorbit(expr, width) — XOR of <paramref name="width"/> bits over the binary
    /// representation of the argument (width rounded up to a multiple of 8).
    /// </summary>
    public static string XorBit(string expr, int width) => $"xorbit({expr}, {width})";

    /// <summary>fixedwidth(field, width) — fixed-width numeric bucket expression for use in group()</summary>
    public static string FixedWidth(string field, double width) =>
        $"fixedwidth({field}, {width.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
}
