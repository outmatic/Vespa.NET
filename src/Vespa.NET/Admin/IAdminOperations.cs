using Vespa.Models;
using Vespa.Models.Schema;

namespace Vespa.Admin;

/// <summary>
/// Administrative operations against the Vespa config server (port 19071).
/// </summary>
public interface IAdminOperations
{
    /// <summary>
    /// Deploys a raw application package ZIP stream to the config server
    /// using the <c>prepareandactivate</c> endpoint.
    /// </summary>
    /// <param name="applicationPackage">ZIP stream containing the application package.</param>
    /// <param name="tenant">Tenant name override. When <see langword="null"/>, uses <see cref="VespaClientOptions.Tenant"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeployAsync(Stream applicationPackage, string? tenant = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a minimal application package from <typeparamref name="T"/>
    /// (using <c>[VespaDocument]</c> / <c>[VespaField]</c> / <c>[VespaTensor]</c>)
    /// and deploys it.
    /// </summary>
    /// <param name="options">Application package options.</param>
    /// <param name="tenant">Tenant name override. When <see langword="null"/>, uses <see cref="VespaClientOptions.Tenant"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeploySchemaAsync<T>(ApplicationPackageOptions? options = null, string? tenant = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Generates a multi-document-type application package and deploys it.
    /// </summary>
    /// <param name="documentTypes">Document types to include in the package.</param>
    /// <param name="options">Application package options.</param>
    /// <param name="tenant">Tenant name override. When <see langword="null"/>, uses <see cref="VespaClientOptions.Tenant"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeploySchemaAsync(IEnumerable<Type> documentTypes, ApplicationPackageOptions? options = null, string? tenant = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the active application status, or <see langword="null"/> on error.
    /// </summary>
    /// <param name="tenant">Tenant name override. When <see langword="null"/>, uses <see cref="VespaClientOptions.Tenant"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<VespaApplicationStatus?> GetApplicationStatusAsync(string? tenant = null, CancellationToken cancellationToken = default);
}
