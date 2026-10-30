using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class AgentFrameworkTests
{
    [Fact]
    public async Task TextQuery_UsesCallerProvidedEmbeddingGenerator()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit()));
        using var embeddings = new FixedEmbeddingGenerator();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = embeddings });
        var results = await store.GetCollection<string, Article>("article").SearchAsync("How does search work?", 1).Collect();
        Assert.Single(results);
        Assert.Equal("How does search work?", Assert.Single(embeddings.Inputs));
        store.Dispose();
        Assert.False(embeddings.Disposed);
    }

    [Fact]
    public async Task RealAgentFramework_InvokesRetrievalToolAndReceivesItsResult()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit()));
        using var embeddings = new FixedEmbeddingGenerator();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = embeddings });
        var collection = store.GetCollection<string, Article>("article");
        var tool = AIFunctionFactory.Create(async (string query, CancellationToken cancellationToken) =>
        {
            var hits = await collection.SearchAsync(query, 3, cancellationToken: cancellationToken).Collect();
            return string.Join("\n", hits.Select(h => $"[{h.Record.Id}] {h.Record.Title}"));
        }, "search_vespa", "Search the documentation in Vespa.");
        using var model = new ToolCallingChatClient();
        AIAgent agent = model.AsAIAgent(instructions: "Answer from retrieved documentation.", tools: [tool]);
        var response = await agent.RunAsync("How does vector search work?");
        Assert.Contains("[a:1] Vespa vector search", response.Text);
        Assert.Equal(2, model.Calls);
        Assert.Single(harness.Handler.Requests);
        Assert.Equal("How does vector search work?", Assert.Single(embeddings.Inputs));
    }
}

// Deterministic model and embeddings keep the real Agent Framework tool loop test
// independent of paid services, network availability, and model behavior.
internal sealed class ToolCallingChatClient : IChatClient
{
    internal int Calls { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        var message = result is null
            ? new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-search", "search_vespa", new Dictionary<string, object?> { ["query"] = "How does vector search work?" })])
            : new ChatMessage(ChatRole.Assistant, result.Result?.ToString());
        return Task.FromResult(new ChatResponse(message));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}

internal sealed class FixedEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    internal List<string> Inputs { get; } = [];
    internal int Calls { get; private set; }
    internal bool Disposed { get; private set; }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        GeneratedEmbeddings<Embedding<float>> result = [];
        foreach (var value in values)
        {
            Inputs.Add(value);
            result.Add(new Embedding<float>(new float[] { 1, 0, 0 }));
        }
        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() => Disposed = true;
}
