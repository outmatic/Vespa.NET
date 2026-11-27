using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Vespa.Documents;
using Vespa.Models;

namespace Vespa.Feed;

/// <summary>One field-update request in a homogeneous bulk batch.</summary>
public sealed record BulkFieldUpdate
{
    public string Id { get; init; } = string.Empty;
    public Dictionary<string, FieldOperation> FieldOperations { get; init; } = [];

    /// <summary>Optional native condition on the stored document. A failed condition produces HTTP 412.</summary>
    public string? Condition { get; init; }

    [JsonIgnore]
    internal string? ExpectedDocumentType { get; init; }

    /// <summary>Creates an update with a shallow copy of the field-operation dictionary and an optional condition.</summary>
    /// <remarks>The batch supplies the namespace. A factory-bound schema is validated by the built-in feed client.</remarks>
    public static BulkFieldUpdate Create<[DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        string documentId,
        IReadOnlyDictionary<string, FieldOperation> fieldOperations,
        string? documentType = null,
        Expression<Func<T, bool>>? condition = null,
        string? rawCondition = null) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(fieldOperations);
        var (type, selector) = FeedEntryCondition.Resolve(documentType, condition, rawCondition);
        return new()
        {
            Id = documentId,
            FieldOperations = new(fieldOperations),
            Condition = selector,
            ExpectedDocumentType = type
        };
    }

    /// <summary>Creates an update using property selectors, freezing the condition before the callback runs.</summary>
    public static BulkFieldUpdate Create<[DynamicallyAccessedMembers(DocumentFieldSelector.ModelMembers)] T>(
        string documentId,
        Action<TypedFieldUpdateBuilder<T>> configure,
        string? documentType = null,
        Expression<Func<T, bool>>? condition = null,
        string? rawCondition = null) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(configure);
        var (type, selector) = FeedEntryCondition.Resolve(documentType, condition, rawCondition);
        var builder = new TypedFieldUpdateBuilder<T>();
        configure(builder);
        return new()
        {
            Id = documentId,
            FieldOperations = new(builder.Build()),
            Condition = selector,
            ExpectedDocumentType = type
        };
    }
}
