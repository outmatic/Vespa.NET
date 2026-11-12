using System.Text.Json;
using Microsoft.Extensions.VectorData;
using Vespa.Models;

namespace Vespa.VectorData;

internal static class VespaCollectionProbe
{
    internal static async Task<bool> ExistsAsync(string name, IVespaCollectionCatalog? catalog, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VespaVectorValidation.Identifier(name, nameof(name));
        if (catalog is null)
            throw new NotSupportedException("CollectionExistsAsync requires an authoritative CollectionCatalog. A search query can resolve a search-chain alias and cannot certify that a schema exists.");
        try
        {
            return await catalog.CollectionExistsAsync(name, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is VespaException or HttpRequestException or JsonException or OperationCanceledException)
        {
            throw Failure(name, nameof(VectorStore.CollectionExistsAsync), exception.Message, exception);
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
                throw Failure(name, operation, string.Join("; ", errors.Select(Describe)));
            if (response.Root.Coverage is { Full: false })
                throw Failure(name, operation, "Vespa returned incomplete search coverage.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is VespaException or HttpRequestException or JsonException or OperationCanceledException)
        {
            var message = exception is VespaException { Error: { } error } ? Describe(error) : exception.Message;
            throw Failure(name, operation, message, exception);
        }
    }

    private static string Describe(VespaError error) => string.IsNullOrWhiteSpace(error.Details)
        ? error.Message : $"{error.Message}: {error.Details}";

    internal static VectorStoreException Failure(string name, string operation, string message, Exception? inner = null) => new(message, inner)
    {
        VectorStoreSystemName = "vespa",
        CollectionName = name,
        OperationName = operation
    };
}
