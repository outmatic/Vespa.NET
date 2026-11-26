namespace Vespa.Query;

internal static class QuotedQueryString
{
    internal static string EscapeContent(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
