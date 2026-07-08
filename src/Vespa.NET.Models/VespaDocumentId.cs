using System.Diagnostics.CodeAnalysis;

namespace Vespa.Models;

/// <summary>
/// Helpers for the Vespa document ID grammar:
/// <c>id:&lt;namespace&gt;:&lt;document-type&gt;:&lt;key/value-pairs&gt;:&lt;user-specified&gt;</c>.
/// The key/value-pairs segment is usually empty (<c>id:ns:type::doc1</c>) or a location
/// selector (<c>g=...</c>/<c>n=...</c>); the user-specified part may contain any character,
/// including <c>:</c>.
/// </summary>
public static class VespaDocumentId
{
    /// <summary>
    /// Returns the user-specified part of a full Vespa document ID, or the input unchanged
    /// when it is not a full document ID (bare IDs, grouping hit IDs like <c>group:root:0</c>, …).
    /// Only the first four <c>:</c>-separated segments are structural — everything after the
    /// fourth colon belongs to the user-specified part.
    /// </summary>
    public static string GetUserSpecified(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith("id:", StringComparison.Ordinal))
            return id;

        var parts = id.Split(':', 5);
        return parts.Length == 5 ? parts[4] : id;
    }

    /// <summary>
    /// Attempts to parse a full Vespa document ID into its structural segments.
    /// Returns <see langword="false"/> for bare IDs and for <c>id:</c>-prefixed strings that
    /// do not follow the grammar (missing segments, empty namespace/type, or a key/value-pairs
    /// segment that is neither empty nor a valid <c>g=</c>/<c>n=</c> location selector).
    /// </summary>
    public static bool TryParse(string id, [NotNullWhen(true)] out VespaDocumentIdParts? parts)
    {
        parts = null;
        if (string.IsNullOrEmpty(id) || !id.StartsWith("id:", StringComparison.Ordinal))
            return false;

        var segments = id.Split(':', 5);
        if (segments.Length != 5 || segments[1].Length == 0 || segments[2].Length == 0)
            return false;

        string? group = null;
        long? number = null;
        var keyValue = segments[3];
        if (keyValue.Length > 0)
        {
            if (keyValue.StartsWith("g=", StringComparison.Ordinal) && keyValue.Length > 2)
                group = keyValue[2..];
            else if (keyValue.StartsWith("n=", StringComparison.Ordinal) && long.TryParse(keyValue[2..], out var n))
                number = n;
            else
                return false;
        }

        parts = new VespaDocumentIdParts(segments[1], segments[2], group, number, segments[4]);
        return true;
    }
}

/// <summary>
/// Structural segments of a full Vespa document ID
/// (<c>id:&lt;namespace&gt;:&lt;document-type&gt;:&lt;key/value-pairs&gt;:&lt;user-specified&gt;</c>).
/// At most one of <see cref="Group"/>/<see cref="Number"/> is non-null (the <c>g=</c>/<c>n=</c>
/// location selector); both are null when the key/value-pairs segment is empty.
/// </summary>
public sealed record VespaDocumentIdParts(
    string Namespace,
    string DocumentType,
    string? Group,
    long? Number,
    string UserSpecified);
