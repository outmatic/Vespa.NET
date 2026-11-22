using System.Linq.Expressions;
using Vespa.Documents;
using Vespa.Models.Attributes;

namespace Vespa.Feed;

internal static class FeedEntryCondition
{
    internal static (string? DocumentType, string? Condition) Resolve<T>(
        string? documentType, Expression<Func<T, bool>>? condition, string? rawCondition) where T : class
    {
        if (condition is not null && rawCondition is not null)
            throw new ArgumentException("Specify either condition or rawCondition, not both.", nameof(rawCondition));
        if (documentType is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        if (condition is null)
            return (documentType, rawCondition);

        var type = documentType ?? VespaDocumentMeta.For<T>().DocumentType;
        return (type, DocumentCondition.Render(type, condition));
    }

    internal static void ValidateTarget(string documentId, string? expectedDocumentType, string documentType)
    {
        if (expectedDocumentType is not null && !string.Equals(expectedDocumentType, documentType, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Feed entry '{documentId}' targets document type '{expectedDocumentType}', but the batch targets '{documentType}'. Create a new entry for the intended batch schema.");
    }
}
