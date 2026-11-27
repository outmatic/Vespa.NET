using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Vespa.Documents;

namespace Vespa.Feed;

/// <summary>Represents a document for feeding.</summary>
public sealed record FeedDocument<T> where T : class
{
    public string Id { get; init; } = string.Empty;
    public T Fields { get; init; } = default!;

    /// <summary>Optional native condition on the stored document. A failed condition produces HTTP 412.</summary>
    public string? Condition { get; init; }

    [JsonIgnore]
    internal string? ExpectedDocumentType { get; init; }
}

/// <summary>Constructs homogeneous feed entries with optional typed or native conditions.</summary>
public static class FeedDocument
{
    /// <summary>Creates a feed entry with an optional typed or native condition.</summary>
    /// <remarks>
    /// A typed condition uses the explicit document type or the model's annotation.
    /// An explicit target also binds unconditional and native-condition entries to that schema.
    /// The built-in feed validates this binding against the batch target. The batch supplies the namespace.
    /// </remarks>
    public static FeedDocument<T> Create<[DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        string documentId,
        T fields,
        string? documentType = null,
        Expression<Func<T, bool>>? condition = null,
        string? rawCondition = null) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(fields);
        var (type, selector) = FeedEntryCondition.Resolve(documentType, condition, rawCondition);
        return new()
        {
            Id = documentId,
            Fields = fields,
            Condition = selector,
            ExpectedDocumentType = type
        };
    }
}
