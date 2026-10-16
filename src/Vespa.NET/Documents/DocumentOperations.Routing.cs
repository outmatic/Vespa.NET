using System.Text;
using Vespa.Models;

namespace Vespa.Documents;

public sealed partial class DocumentOperations
{
    private string ResolveNamespace(string? @namespace) =>
        @namespace ?? _options.DefaultNamespace;

    /// <summary>
    /// Escaped <c>/document/v1/{namespace}/{documentType}</c> prefix. Both segments are
    /// escaped so user-supplied values cannot inject path segments (<c>../</c>, <c>/</c>).
    /// </summary>
    private static string DocTypePrefix(string documentType, string ns) =>
        $"{VespaPaths.DocV1}/{Uri.EscapeDataString(ns)}/{Uri.EscapeDataString(documentType)}";

    private string DocBasePath(string documentType, string? @namespace) =>
        $"{DocTypePrefix(documentType, ResolveNamespace(@namespace))}/docid/";

    /// <summary>
    /// Builds the URL for a by-document-ID operation. Full Vespa IDs
    /// (<c>id:ns:type:kv:user</c>) are parsed structurally: <c>g=</c>/<c>n=</c> location
    /// selectors route to the <c>group/…</c>/<c>number/…</c> path levels, and a namespace
    /// or document type differing from the operation's target throws
    /// <see cref="ArgumentException"/> instead of silently re-targeting another document.
    /// Bare IDs pass through unchanged.
    /// </summary>
    private (string Namespace, string Url) BuildDocIdUrl(
        string documentType,
        string documentId,
        string? @namespace,
        DocumentRequestOptions? requestOptions,
        params (string Key, string? Value)[] extraParams)
    {
        var ns = ResolveNamespace(@namespace);
        var prefix = DocTypePrefix(documentType, ns);
        string path;

        if (VespaDocumentId.TryParse(documentId, out var parts))
        {
            if (!string.Equals(parts.Namespace, ns, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Document ID '{documentId}' addresses namespace '{parts.Namespace}', but the operation targets namespace '{ns}'.",
                    nameof(documentId));
            if (!string.Equals(parts.DocumentType, documentType, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Document ID '{documentId}' addresses document type '{parts.DocumentType}', but the operation targets document type '{documentType}'.",
                    nameof(documentId));

            path = parts switch
            {
                { Group: not null } => $"{prefix}/group/{Uri.EscapeDataString(parts.Group)}/{Uri.EscapeDataString(parts.UserSpecified)}",
                { Number: not null } => $"{prefix}/number/{parts.Number}/{Uri.EscapeDataString(parts.UserSpecified)}",
                _ => $"{prefix}/docid/{Uri.EscapeDataString(parts.UserSpecified)}"
            };
        }
        else
            path = $"{prefix}/docid/{Uri.EscapeDataString(VespaDocumentId.GetUserSpecified(documentId))}";

        return (ns, BuildUrl(path, requestOptions, extraParams));
    }

    private static string BuildUrl(string basePath, DocumentRequestOptions? requestOptions, params (string Key, string? Value)[] queryParams)
    {
        var sb = new StringBuilder(basePath);
        var first = true;

        void Append((string Key, string? Value) p)
        {
            if (p.Value is null) return;
            sb.Append(first ? '?' : '&');
            first = false;
            sb.Append(p.Key).Append('=').Append(Uri.EscapeDataString(p.Value));
        }

        if (requestOptions is not null)
            foreach (var p in requestOptions.ToQueryParams())
            {
                // Explicit method parameters win over request options — emitting the
                // same key twice (e.g. timeout) is undefined behaviour server-side.
                if (Array.Exists(queryParams, q => q.Value is not null && q.Key == p.Key))
                    continue;
                Append(p);
            }

        foreach (var p in queryParams)
            Append(p);

        return sb.ToString();
    }
}
