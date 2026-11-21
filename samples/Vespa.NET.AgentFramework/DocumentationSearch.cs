using Microsoft.Extensions.AI;
using Vespa.Models;

namespace Vespa.AgentFrameworkSample;

internal sealed class DocumentationSearch(IVespaClient client,
    IEmbeddingGenerator<string, Embedding<float>> embeddings, RetrievalSettings settings)
{
    internal async Task<IReadOnlyList<SearchHit<Article>>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        settings.Validate(3);
        var vector = settings.Mode == "lexical" ? null
            : (await embeddings.GenerateVectorAsync("search_query: " + query, cancellationToken: cancellationToken)).ToArray();
        var request = NativeRetrieval.CreateRequest(query, vector, "article",
            "published = true and category contains \"agent-framework\"", settings);
        var response = await client.Search.SearchAsync<Article>(request, cancellationToken);
        if (response.Root.Errors is { Count: > 0 } errors)
            throw new InvalidOperationException(string.Join("; ", errors.Select(e => e.Message)));
        if (response.Root.Coverage is not { Full: true })
            throw new InvalidOperationException("Vespa returned incomplete search coverage.");
        if (response.Root.Children.Any(hit => hit.Fields is null ||
            string.IsNullOrWhiteSpace(hit.Fields.Title) && string.IsNullOrWhiteSpace(hit.Fields.Body)))
            throw new InvalidOperationException("The article summary is missing its supporting text.");
        return response.Root.Children;
    }
}
