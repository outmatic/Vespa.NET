namespace Vespa.Documents;

internal static class VespaIdentifier
{
    internal static void ValidateDocumentType(string documentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        var valid = (char.IsAsciiLetter(documentType[0]) || documentType[0] == '_') &&
            documentType.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        if (!valid || documentType.ToLowerInvariant() is "true" or "false" or "and" or "or" or "not" or "null" or "id")
            throw new ArgumentException($"'{documentType}' is not a valid document type identifier.", nameof(documentType));
    }
}
