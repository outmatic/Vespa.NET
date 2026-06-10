using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Vespa.Models;
using Vespa.Models.Schema;

namespace Vespa.Admin;

/// <summary>
/// Implementation of Vespa config-server administrative operations.
/// </summary>
/// <param name="httpClient">HTTP client pointed at the config server.</param>
/// <param name="options">Client options.</param>
/// <param name="logger">Optional logger.</param>
/// <param name="ownsHttpClient">
/// When <see langword="true"/>, <see cref="Dispose"/> disposes <paramref name="httpClient"/>.
/// Defaults to <see langword="false"/> so externally-managed (e.g. factory) clients
/// are never killed by this instance.
/// </param>
public sealed partial class AdminOperations(
    HttpClient httpClient,
    VespaClientOptions options,
    ILogger? logger = null,
    bool ownsHttpClient = false) : IAdminOperations, IDisposable
{
    private readonly HttpClient _httpClient =
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    private readonly VespaClientOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    private string ResolveTenant(string? tenant) => tenant ?? _options.Tenant;

    private string PrepareAndActivatePath(string tenant) =>
        $"/application/v2/tenant/{Uri.EscapeDataString(tenant)}/prepareandactivate";

    private string ApplicationStatusPath(string tenant) =>
        $"/application/v2/tenant/{Uri.EscapeDataString(tenant)}/application/default";

    public async Task DeployAsync(Stream applicationPackage, string? tenant = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applicationPackage);

        var resolvedTenant = ResolveTenant(tenant);

        if (logger is not null)
            LogDeploying(logger, applicationPackage.CanSeek ? applicationPackage.Length : -1);

        using var content = new StreamContent(applicationPackage);
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");

        using var response = await _httpClient.PostAsync(PrepareAndActivatePath(resolvedTenant), content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw VespaException.FromStatusCode(
                (int)response.StatusCode,
                $"Deploy failed with status {response.StatusCode}: {error}");
        }

        if (logger is not null)
            LogDeployed(logger);
    }

    public Task DeploySchemaAsync<T>(ApplicationPackageOptions? options = null, string? tenant = null, CancellationToken cancellationToken = default) where T : class =>
        DeployViaTemporaryFileAsync(fs => VespaSchemaBuilder.GenerateApplicationPackage<T>(fs, options), tenant, cancellationToken);

    public Task DeploySchemaAsync(IEnumerable<Type> documentTypes, ApplicationPackageOptions? options = null, string? tenant = null, CancellationToken cancellationToken = default) =>
        DeployViaTemporaryFileAsync(fs => VespaSchemaBuilder.GenerateApplicationPackage(fs, documentTypes, options), tenant, cancellationToken);

    private async Task DeployViaTemporaryFileAsync(Action<FileStream> generatePackage, string? tenant, CancellationToken cancellationToken)
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                generatePackage(fs);

            await using var readFs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
            await DeployAsync(readFs, tenant, cancellationToken);
        }
        finally
        {
            // Best-effort cleanup — a failure here must never mask the deploy exception
            try { File.Delete(tempPath); }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { }
        }
    }

    public async Task<VespaApplicationStatus?> GetApplicationStatusAsync(string? tenant = null, CancellationToken cancellationToken = default)
    {
        var resolvedTenant = ResolveTenant(tenant);

        try
        {
            using var response = await _httpClient.GetAsync(
                ApplicationStatusPath(resolvedTenant), HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<VespaApplicationStatus>(
                VespaJsonOptions.Default, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (logger is not null)
                LogStatusFailed(logger, ex);
            return null;
        }
    }

    public void Dispose()
    {
        if (ownsHttpClient)
            _httpClient.Dispose();
    }

    [LoggerMessage(EventId = 401, Level = LogLevel.Information,
        Message = "Deploying application package ({Bytes} bytes)")]
    static partial void LogDeploying(ILogger logger, long bytes);

    [LoggerMessage(EventId = 402, Level = LogLevel.Information,
        Message = "Application package deployed successfully")]
    static partial void LogDeployed(ILogger logger);

    [LoggerMessage(EventId = 403, Level = LogLevel.Warning,
        Message = "Failed to retrieve application status")]
    static partial void LogStatusFailed(ILogger logger, Exception ex);
}
