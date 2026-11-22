using System.Linq.Expressions;
using System.Text;
using Vespa.Documents;
using Vespa.Models;

namespace Vespa.Query;

/// <summary>Builds native streaming-search selections without changing the YQL query.</summary>
public static class StreamingSearchExtensions
{
    /// <summary>Sets a frozen document selector scoped to the filter model's document type.</summary>
    /// <remarks>Field predicates can scan all groups. For location-based searches, use StreamingGroupName or WithStreamingGroups.</remarks>
    public static VespaSearchRequest WithStreamingSelection<T>(
        this VespaSearchRequest request,
        Expression<Func<T, bool>> selection) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selection);
        ValidateStreamingLocation(request);
        var selector = DocumentSelection.For(selection);
        request.StreamingSelection = selector;
        return request;
    }

    /// <summary>Selects streaming groups using a native disjunction of exact id.group comparisons.</summary>
    /// <remarks>
    /// Group names are escaped, deduplicated and frozen when called. This sets only StreamingSelection; YQL still filters the results.
    /// One group ending in a backslash is placed last. Multiple distinct such names are not supported by Vespa's selector parser.
    /// </remarks>
    public static VespaSearchRequest WithStreamingGroups(this VespaSearchRequest request, params string[] groupNames)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(groupNames);
        if (groupNames.Length == 0)
            throw new ArgumentException("Specify at least one streaming group.", nameof(groupNames));
        ValidateStreamingLocation(request);
        List<string> clauses = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        string? terminalBackslashClause = null;
        for (var i = 0; i < groupNames.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(groupNames[i]))
                throw new ArgumentException($"Streaming group at index {i} must not be blank.", nameof(groupNames));
            if (!seen.Add(groupNames[i]))
                continue;
            var clause = $"id.group == {QuoteGroupName(groupNames[i])}";
            if (groupNames[i].EndsWith('\\'))
            {
                if (terminalBackslashClause is not null)
                    throw new NotSupportedException("Vespa's group-selector parser cannot safely combine multiple distinct group names ending in a backslash. Use separate requests or an explicit native selection.");
                terminalBackslashClause = clause;
            }
            else
                clauses.Add(clause);
        }

        // Java's selector lexer can consume a following literal after an escaped
        // terminal backslash. Keep that clause last without changing the group set.
        if (terminalBackslashClause is not null)
            clauses.Add(terminalBackslashClause);
        request.StreamingSelection = string.Join(" or ", clauses);
        return request;
    }

    private static string QuoteGroupName(string groupName)
    {
        // Both the Java bucket selector and C++ document selector read this literal.
        // Keep Unicode characters intact: Java does not interpret C++ byte escapes.
        var quoted = new StringBuilder("\"");
        for (var i = 0; i < groupName.Length; i++)
        {
            var value = groupName[i];
            if (char.IsSurrogate(value))
            {
                if (!char.IsHighSurrogate(value) || i + 1 >= groupName.Length || !char.IsLowSurrogate(groupName[i + 1]))
                    throw new ArgumentException("Streaming group names must contain valid Unicode.", nameof(groupName));
                if (!IsGroupTextCharacter(char.ConvertToUtf32(value, groupName[i + 1])))
                    throw new ArgumentException("Streaming group names must contain valid Vespa text characters.", nameof(groupName));
                quoted.Append(value).Append(groupName[++i]);
                continue;
            }

            if (!IsGroupTextCharacter(value))
                throw new ArgumentException("Streaming group names must contain valid Vespa text characters.", nameof(groupName));

            switch (value)
            {
                case '"': quoted.Append("\\\""); break;
                case '\\': quoted.Append("\\\\"); break;
                case '\n': quoted.Append("\\n"); break;
                case '\r': quoted.Append("\\r"); break;
                case '\t': quoted.Append("\\t"); break;
                default:
                    quoted.Append(value);
                    break;
            }
        }
        return quoted.Append('"').ToString();
    }

    private static bool IsGroupTextCharacter(int value) =>
        value is '\t' or '\n' or '\r' ||
        value >= 0x20 && value is not (>= 0xFDD0 and <= 0xFDDF) && (value & 0xFFFF) < 0xFFFE;

    private static void ValidateStreamingLocation(VespaSearchRequest request)
    {
        if (request.StreamingGroupName is not null)
            throw new InvalidOperationException("A streaming selection cannot be combined with StreamingGroupName. Clear the group name before setting a selection.");
        if (request.CustomParameters is { } parameters &&
            (parameters.ContainsKey("streaming.selection") || parameters.ContainsKey("streaming.groupname") || parameters.ContainsKey("streaming.userid")))
            throw new InvalidOperationException("Streaming location parameters in CustomParameters conflict with this selection. Remove those parameters before setting a selection.");
    }
}
