using Vespa.Models.Schema;

namespace Vespa.Models.Attributes;

/// <summary>
/// Attribute to specify Vespa document type metadata for schema generation
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public class VespaDocumentAttribute : Attribute
{
    /// <summary>
    /// Document type name in Vespa schema
    /// </summary>
    public string DocumentType { get; }

    /// <summary>
    /// Whether this is a global document (replicated to all content nodes)
    /// </summary>
    public bool Global { get; set; }

    /// <summary>
    /// Namespace for this document type
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Schema this schema inherits from (e.g. <c>"base_schema"</c>).
    /// </summary>
    public string? Inherits { get; set; }

    /// <summary>
    /// Document selection expression for document expiry (garbage collection):
    /// documents <em>not</em> matching the expression are removed.
    /// Example: <c>"music.timestamp > now() - 86400"</c>.
    /// Rendered into the generated <c>services.xml</c> as
    /// <c>&lt;document … selection="…"/&gt;</c> with <c>garbage-collection="true"</c>
    /// on the <c>&lt;documents&gt;</c> element — not into the <c>.sd</c> schema,
    /// where Vespa does not accept it.
    /// </summary>
    public string? Selection { get; set; }

    /// <summary>
    /// Document mode in <c>services.xml</c>. Default: <see cref="DocumentMode.Index"/>.
    /// </summary>
    public DocumentMode Mode { get; set; } = DocumentMode.Index;

    /// <summary>
    /// Create a VespaDocument attribute with the specified document type
    /// </summary>
    /// <param name="documentType">Document type name in Vespa schema</param>
    public VespaDocumentAttribute(string documentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        DocumentType = documentType;
    }
}
