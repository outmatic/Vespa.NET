namespace Vespa.Models;

internal static class IdentifierSyntax
{
    internal static bool IsStart(char character) => char.IsAsciiLetter(character) || character == '_';

    internal static bool IsPart(char character) => char.IsAsciiLetterOrDigit(character) || character == '_';

    internal static bool IsSimple(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || !IsStart(value[0]))
            return false;

        foreach (var character in value[1..])
            if (!IsPart(character))
                return false;

        return true;
    }
}
