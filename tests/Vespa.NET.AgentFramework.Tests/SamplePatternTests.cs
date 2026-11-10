#pragma warning disable MAAI001
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Vespa.AgentFrameworkSample;
using Vespa.VectorData;
using Xunit;

namespace Vespa.AgentFramework.Tests;

public sealed class SamplePatternTests
{
    [Theory]
    [InlineData("session")]
    [InlineData("history")]
    [InlineData("compaction")]
    [InlineData("file-memory")]
    [InlineData("harness")]
    public async Task RunnablePatterns_RestoreTheConversationAcrossCalls(string pattern)
    {
        using var fixture = new StateFixture();
        using var embeddings = new SampleEmbeddings();
        using var vectors = new VespaVectorStore(fixture.Client, new() { Namespace = "knowledge", RankProfile = "vector_search", EmbeddingGenerator = embeddings });
        using var model = new PatternChatClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ConversationPatterns.RunAsync(pattern, "sliding", false, false, false, "First turn.",
            fixture.Client, model, embeddings, vectors, timeout.Token);
        await ConversationPatterns.RunAsync(pattern, "sliding", false, false, true, "Continue.",
            fixture.Client, model, embeddings, vectors, timeout.Token);
        Assert.Contains(model.LastMessages, m => m.Text == "First turn.");
    }

    [Fact]
    public async Task CustomContextProvider_InjectsThePreferenceAndPersistsPerSessionRunState()
    {
        using var fixture = new StateFixture();
        using var embeddings = new SampleEmbeddings();
        using var vectors = new VespaVectorStore(fixture.Client, new() { Namespace = "knowledge", RankProfile = "vector_search", EmbeddingGenerator = embeddings });
        using var memories = vectors.GetCollection<string, MemoryRecord>("memory");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var memory = new PreferenceMemory(memories, embeddings, "demo-user");
        await memory.RememberAsync("Use C# examples.", timeout.Token);
        var provider = new PreferenceContextProvider(memories, embeddings, _ => new() { UserId = "demo-user" });
        using var model = new PatternChatClient();
        var agent = model.AsAIAgent(new ChatClientAgentOptions { Name = "custom-context", AIContextProviders = [provider] });
        var session = await agent.CreateSessionAsync(timeout.Token);
        await agent.RunAsync("What do I prefer?", session, cancellationToken: timeout.Token);
        Assert.Contains(model.LastMessages, m => m.Text.Contains("Stored code-example preference: Use C# examples.", StringComparison.Ordinal));
        var serialized = await agent.SerializeSessionAsync(session, cancellationToken: timeout.Token);
        var resumed = await agent.DeserializeSessionAsync(serialized, cancellationToken: timeout.Token);
        var state = resumed.StateBag.GetValue<PreferenceContextProvider.State>(nameof(PreferenceContextProvider));
        Assert.NotNull(state);
        Assert.Equal("demo-user", state.UserId);
        Assert.Equal(1, state.CompletedRuns);
    }
}

internal sealed class SampleEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GeneratedEmbeddings<Embedding<float>> results = [];
        foreach (var _ in values)
        {
            var vector = new float[768];
            vector[0] = 1;
            results.Add(new(vector));
        }
        return Task.FromResult(results);
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
