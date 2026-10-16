namespace Vespa.Models;

/// <summary>The document mutation performed by a feed operation.</summary>
public enum FeedOperationKind
{
    /// <summary>Insert or replace a document.</summary>
    Put,

    /// <summary>Apply field-level updates.</summary>
    Update,

    /// <summary>Remove a document.</summary>
    Delete
}
