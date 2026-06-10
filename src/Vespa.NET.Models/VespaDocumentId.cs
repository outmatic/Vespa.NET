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
}
