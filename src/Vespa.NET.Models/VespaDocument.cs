using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Represents a Vespa document with typed fields
/// </summary>
/// <typeparam name="T">Type of the document fields</typeparam>
public sealed record VespaDocument<T> where T : class
{
    private readonly string? _id;

    /// <summary>
    /// Full document ID exactly as returned by Vespa
    /// (e.g. <c>id:mynamespace:mytype::doc1</c>). Set during deserialization.
    /// </summary>
    [JsonPropertyName("id")]
    public string FullId { get; init; } = string.Empty;

    /// <summary>
    /// User-specified (short) part of the document ID (e.g. <c>doc1</c>).
    /// Derived from <see cref="FullId"/> unless set explicitly; strings that are
    /// not full document IDs pass through unchanged.
    /// </summary>
    [JsonIgnore]
    public string Id
    {
        get => _id ?? VespaDocumentId.GetUserSpecified(FullId);
        init => _id = value;
    }

    [JsonPropertyName("fields")]
    public T? Fields { get; init; }

    [JsonPropertyName("pathId")]
    public string? PathId { get; init; }
}
