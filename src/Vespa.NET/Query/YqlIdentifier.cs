namespace Vespa.Query;

/// <summary>
/// Validation for names interpolated verbatim into YQL/grouping expressions.
/// String <em>values</em> are escaped by the predicates; field and tensor
/// <em>names</em> cannot be escaped, so they are restricted to the identifier
/// grammar (<c>[A-Za-z_][A-Za-z0-9_]*</c>, dot-separated for struct paths)
/// to keep user-supplied input from breaking out of the query.
/// </summary>
internal static class YqlIdentifier
{
    /// <summary>Validates and returns <paramref name="name"/>; throws <see cref="ArgumentException"/> otherwise.</summary>
    internal static string Validate(string name, string paramName)
    {
        if (!IsValid(name))
            throw new ArgumentException(
                $"'{name}' is not a valid YQL identifier. Expected [A-Za-z_][A-Za-z0-9_]* segments, optionally dot-separated (e.g. \"price\" or \"attributes.key\").",
                paramName);
        return name;
    }

    internal static bool IsValid(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        var startOfSegment = true;
        foreach (var c in name)
        {
            if (startOfSegment)
            {
                if (!char.IsAsciiLetter(c) && c != '_')
                    return false;
                startOfSegment = false;
            }
            else if (c == '.')
            {
                startOfSegment = true;
            }
            else if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return !startOfSegment; // no trailing dot / empty segment
    }
}
