using Vespa.Models;

namespace Vespa.Search;

/// <summary>
/// Helpers for grouping pagination with the YQL <c>continuations</c> annotation
/// or the <c>continue</c> parameter for a separate <c>select</c> expression.
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

        ValidateTokenValues(tokens);

        var insertAt = pipeIdx + 1;
        while (insertAt < yql.Length && yql[insertAt] == ' ')
            insertAt++;

        var annotation = $"{{ 'continuations':[{string.Join(", ", tokens.Select(t => $"'{t}'"))}] }}";
        return yql.Insert(insertAt, annotation);
    }

    internal static void ApplyToRequest(VespaSearchRequest request, IReadOnlyList<string> tokens)
    {
        if (!string.IsNullOrWhiteSpace(request.Select) && !string.IsNullOrWhiteSpace(request.Yql)
            && FindPipeOutsideStringLiterals(request.Yql) < 0)
        {
            ArgumentOutOfRangeException.ThrowIfZero(tokens.Count, nameof(tokens));
            ValidateTokenValues(tokens);
            request.WithParameter("continue", string.Join(' ', tokens));
        }
        else
            request.Yql = Apply(request.Yql, tokens);
    }

    private static void ValidateTokenValues(IReadOnlyList<string> tokens)
    {
        foreach (var token in tokens)
            if (string.IsNullOrEmpty(token) || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '='))
                throw new ArgumentException($"'{token}' is not a valid continuation token.", nameof(tokens));
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
