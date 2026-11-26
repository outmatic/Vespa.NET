using Vespa.Models;

namespace Vespa.Documents;

internal static class VespaIdentifier
{
    internal static void ValidateDocumentType(string documentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        if (!IdentifierSyntax.IsSimple(documentType) || documentType.ToLowerInvariant() is "true" or "false" or "and" or "or" or "not" or "null" or "id")
            throw new ArgumentException($"'{documentType}' is not a valid document type identifier.", nameof(documentType));
    }
}
