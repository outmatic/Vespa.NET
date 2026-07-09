namespace Vespa.Search;

/// <summary>
/// Helpers for grouping pagination. Vespa has no <c>grouping.continuation</c> query
/// parameter: continuation tokens are passed back inside the YQL via the
/// <c>continuations</c> annotation on the grouping step —
/// <c>… | {{ 'continuations':['this-token', 'next-token', …] }}all(…)</c>
/// (docs.vespa.ai/en/grouping.html, "Pagination").
/// </summary>
public static class GroupingContinuations
{
    /// <summary>
    /// Returns <paramref name="yql"/> with the <c>continuations</c> annotation inserted
    /// before the grouping expression. <paramref name="tokens"/> must contain the root
    /// group's <c>this</c> token first, followed by the group lists' <c>next</c> tokens —
    /// exactly what <c>GroupingSearchResponse.ContinuationTokens</c> carries.
    /// </summary>
    public static string Apply(string yql, IReadOnlyList<string> tokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yql);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentOutOfRangeException.ThrowIfZero(tokens.Count, nameof(tokens));

        var pipeIdx = FindPipeOutsideStringLiterals(yql);
        if (pipeIdx < 0)
            throw new ArgumentException(
                "The YQL contains no grouping expression ('|' pipe) to attach continuation tokens to.",
                nameof(yql));

        foreach (var token in tokens)
            if (string.IsNullOrEmpty(token) || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '='))
                throw new ArgumentException($"'{token}' is not a valid continuation token.", nameof(tokens));

        var insertAt = pipeIdx + 1;
        while (insertAt < yql.Length && yql[insertAt] == ' ')
            insertAt++;

        var annotation = $"{{ 'continuations':[{string.Join(", ", tokens.Select(t => $"'{t}'"))}] }}";
        return yql.Insert(insertAt, annotation);
    }

    /// <summary>
    /// Index of the first <c>|</c> outside single/double-quoted string literals —
    /// a pipe inside e.g. <c>title contains "a|b"</c> is not the grouping separator.
    /// </summary>
    private static int FindPipeOutsideStringLiterals(string yql)
    {
        char? quote = null;
        for (var i = 0; i < yql.Length; i++)
        {
            var c = yql[i];
            if (quote is { } q)
            {
                if (c == '\\')
                    i++; // skip the escaped character
                else if (c == q)
                    quote = null;
            }
            else if (c is '"' or '\'')
                quote = c;
            else if (c == '|')
                return i;
        }
        return -1;
    }
}
