using System.Text.Json;
using Microsoft.Extensions.VectorData;
using Vespa.Models;

namespace Vespa.VectorData;

internal static class VespaCollectionProbe
{
    internal static async Task<bool> ExistsAsync(string name, IVespaCollectionCatalog? catalog, string operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VespaVectorValidation.Identifier(name, nameof(name));
        if (catalog is null)
            throw new NotSupportedException("CollectionExistsAsync requires an authoritative CollectionCatalog. A search query can resolve a search-chain alias and cannot certify that a schema exists.");
        try
        {
            return await catalog.CollectionExistsAsync(name, cancellationToken);
        }
        catch (Exception exception) when (VespaVectorErrors.IsOperational(exception, cancellationToken))
        {
            throw VespaVectorErrors.Create(name, operation, exception.Message, exception);
        }
    }

    internal static async Task ValidateSearchabilityAsync(IVespaClient client, string name, TimeSpan timeout,
        string operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var response = await client.Search.SearchAsync<Dictionary<string, JsonElement>>(new VespaSearchRequest
            {
                Yql = $"select documentid from sources {name} where true",
                ModelRestrict = name,
                Hits = 0,
                Ranking = new RankingConfig { Profile = "unranked" }
            }.WithTimeout(timeout), cancellationToken);
            if (response.Root.Errors is { Count: > 0 } errors)
                throw VespaVectorErrors.Create(name, operation, string.Join("; ", errors.Select(Describe)));
            if (response.Root.Coverage is { Full: false })
                throw VespaVectorErrors.Create(name, operation, "Vespa returned incomplete search coverage.");
        }
        catch (Exception exception) when (VespaVectorErrors.IsOperational(exception, cancellationToken))
        {
            var message = exception is VespaException { Error: { } error } ? Describe(error) : exception.Message;
            throw VespaVectorErrors.Create(name, operation, message, exception);
        }
    }

    private static string Describe(VespaError error) => string.IsNullOrWhiteSpace(error.Details)
        ? error.Message : $"{error.Message}: {error.Details}";
}

internal static class VespaVectorErrors
{
    internal static bool IsOperational(Exception exception, CancellationToken cancellationToken) =>
        exception is VespaException or HttpRequestException or JsonException ||
        exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    internal static VectorStoreException Create(string? collectionName, string operation, string message,
        Exception? innerException = null) => new(message, innerException)
        {
            VectorStoreSystemName = "vespa",
            CollectionName = collectionName,
            OperationName = operation
        };
}
