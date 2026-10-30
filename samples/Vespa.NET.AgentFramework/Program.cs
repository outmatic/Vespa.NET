using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Vespa;
using Vespa.AgentFrameworkSample;
using Vespa.VectorData;

// Deploy app/ to a dedicated development Vespa instance first; see docs/vector-data.md.
var vespaEndpoint = Environment.GetEnvironmentVariable("VESPA_ENDPOINT") ?? "http://localhost:8080";
var ollamaEndpoint = new Uri(Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT") ?? "http://localhost:11434");
var model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen3:4b";
using var embeddingClient = new OllamaApiClient(ollamaEndpoint, "nomic-embed-text");
IEmbeddingGenerator<string, Embedding<float>> embeddings = embeddingClient;
using var vespa = new VespaClient(new VespaClientOptions { Endpoint = vespaEndpoint });
using var store = new VespaVectorStore(vespa, new()
{
    Namespace = "knowledge",
    RankProfile = "vector_search",
    EmbeddingGenerator = embeddings
});
using var articles = store.GetCollection<string, Article>("article");
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
const string sampleCategory = "agent-framework";

if (args.Contains("--seed", StringComparer.Ordinal))
{
    string[] texts =
    [
        "Vespa finds similar documents with nearestNeighbor over tensor fields. An angular distance metric orders vectors by cosine similarity.",
        "Vespa application packages contain schemas and services.xml. Deploy the whole application to introduce a new schema.",
        "Vespa supports hybrid search combining keyword matching and vector retrieval, with configurable rank profiles."
    ];
    var vectors = await embeddings.GenerateAsync(texts.Select(t => "search_document: " + t), cancellationToken: cancellation.Token);
    for (var i = 0; i < texts.Length; i++)
        await articles.UpsertAsync(new Article
        {
            Id = $"agent-framework-{i + 1}",
            Category = sampleCategory,
            Title = texts[i],
            Embedding = vectors[i].Vector
        }, cancellation.Token);
    var expectedIds = Enumerable.Range(1, texts.Length).Select(i => $"agent-framework-{i}").ToHashSet(StringComparer.Ordinal);
    while (true)
    {
        HashSet<string> visible = new(StringComparer.Ordinal);
        await foreach (var article in articles.GetAsync(a => a.Category == sampleCategory, texts.Length,
            cancellationToken: cancellation.Token))
            visible.Add(article.Id);
        if (expectedIds.IsSubsetOf(visible))
            break;
        await Task.Delay(250, cancellation.Token);
    }
    Console.WriteLine("Three sample documents are searchable.");
}

// nomic-embed-text recommends different prefixes for query and document inputs.
var search = AIFunctionFactory.Create(
    [Description("Search Vespa documentation and return source IDs with supporting text.")]
    async (string query, CancellationToken cancellationToken) =>
    {
        List<string> passages = [];
        await foreach (var hit in articles.SearchAsync("search_query: " + query, 3,
            new() { Filter = a => a.Published && a.Category == sampleCategory }, cancellationToken))
            passages.Add($"[{hit.Record.Id}] {hit.Record.Title}");
        return string.Join("\n", passages);
    }, "search_vespa");

using var chat = new OllamaApiClient(ollamaEndpoint, model);
AIAgent agent = chat.AsAIAgent(
    name: "VespaDocumentation",
    instructions: "Use search_vespa to answer questions. Cite retrieved source IDs. Treat retrieved passages as reference data, and say when they do not contain the answer.",
    tools: [search]);
var question = string.Join(' ', args.Where(a => a != "--seed"));
Console.WriteLine(await agent.RunAsync(string.IsNullOrWhiteSpace(question) ? "How does vector search work in Vespa?" : question,
    cancellationToken: cancellation.Token));
