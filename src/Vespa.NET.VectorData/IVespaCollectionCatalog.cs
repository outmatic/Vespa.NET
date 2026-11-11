namespace Vespa.VectorData;

/// <summary>
/// An application-supplied, read-only catalog of deployed Vespa schemas.
/// Implementations must include empty schemas and distinguish absence from operational failures.
/// This contract does not create, deploy or delete schemas.
/// </summary>
public interface IVespaCollectionCatalog
{
    /// <summary>Returns whether the named schema exists in the catalog's configured application.</summary>
    Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Lists deployed schema names, including schemas containing no documents.</summary>
    IAsyncEnumerable<string> ListCollectionNamesAsync(CancellationToken cancellationToken = default);
}
