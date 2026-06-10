namespace Vespa.Query;

/// <summary>
/// Validation for names interpolated verbatim into YQL/grouping expressions.
/// String <em>values</em> are escaped by the predicates; field and tensor
/// <em>names</em> cannot be escaped, so they are restricted to the identifier
/// grammar — dot-separated <c>[A-Za-z_][A-Za-z0-9_]*</c> segments, each
/// optionally followed by a map-key suffix (<c>field{"key"}</c>) — to keep
/// user-supplied input from breaking out of the query.
/// </summary>
internal static class YqlIdentifier
{
    /// <summary>Validates and returns <paramref name="name"/>; throws <see cref="ArgumentException"/> otherwise.</summary>
    internal static string Validate(string name, string paramName)
    {
        if (!IsValid(name))
            throw new ArgumentException(
                $"'{name}' is not a valid YQL identifier. Expected [A-Za-z_][A-Za-z0-9_]* segments, optionally dot-separated, with an optional map-key suffix (e.g. \"price\", \"attributes.key\" or \"my_map{{\"key\"}}\").",
                paramName);
        return name;
    }

    internal static bool IsValid(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        var i = 0;
        while (true)
        {
            // identifier segment
            if (i >= name.Length || (!char.IsAsciiLetter(name[i]) && name[i] != '_'))
                return false;
            i++;
            while (i < name.Length && (char.IsAsciiLetterOrDigit(name[i]) || name[i] == '_'))
                i++;

            // optional map-key suffix: {"key"} — key may not contain quotes/backslashes
            if (i < name.Length && name[i] == '{')
            {
                if (i + 1 >= name.Length || name[i + 1] != '"')
                    return false;
                i += 2;
                while (i < name.Length && name[i] is not '"' and not '\\')
                    i++;
                if (i >= name.Length || name[i] != '"')
                    return false;
                i++;
                if (i >= name.Length || name[i] != '}')
                    return false;
                i++;
            }

            if (i == name.Length)
                return true;
            if (name[i] != '.')
                return false;
            i++;
        }
    }
}
