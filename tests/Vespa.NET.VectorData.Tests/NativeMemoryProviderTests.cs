using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class NativeMemoryProviderTests
{
    [Fact]
    public async Task MicrosoftProvider_PersistsAndRecallsAcrossNewClientsAgentsAndSessions()
    {
        const string preference = "Please use C# for my examples.";
        var backend = new NativeHistoryBackend();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var harness = new TestHarness(backend.Respond))
        using (var embeddings = new BatchEmbeddingGenerator())
        using (var store = new VespaVectorStore(harness.Client, TestHarness.Options with
        {
            EmbeddingGenerator = embeddings,
            DocumentEmbeddingPrefix = "search_document: ",
            QueryEmbeddingPrefix = "search_query: "
        }))
        using (var provider = CreateProvider(store, "alice", "first-session"))
        using (var model = new NativeContextChatClient())
        {
            var agent = model.AsAIAgent(new ChatClientAgentOptions { AIContextProviders = [provider] });
            await agent.RunAsync(preference, cancellationToken: timeout.Token);
            Assert.Equal(2, backend.Records.Count);
            Assert.Equal(2, embeddings.Calls); // One query batch and one message batch.
            Assert.Equal(new[] { "search_query: " + preference, "search_document: " + preference, "search_document: Acknowledged." }, embeddings.Inputs);
            Assert.Equal(1, backend.SchemaChecks);
            foreach (var (key, fields) in backend.Records)
            {
                Assert.Equal(key, Guid.Parse(key).ToString("D"));
                Assert.Equal(3, fields.GetProperty("ContentEmbedding").GetProperty("values").GetArrayLength());
            }
        }

        using var nextHarness = new TestHarness(backend.Respond);
        using var nextEmbeddings = new BatchEmbeddingGenerator();
        using var nextStore = new VespaVectorStore(nextHarness.Client, TestHarness.Options with { EmbeddingGenerator = nextEmbeddings });
        using var nextProvider = CreateProvider(nextStore, "alice", "second-session");
        using var nextModel = new NativeContextChatClient();
        var nextAgent = nextModel.AsAIAgent(new ChatClientAgentOptions { AIContextProviders = [nextProvider] });
        await nextAgent.RunAsync("Which language should you use?", cancellationToken: timeout.Token);
        Assert.Contains("NATIVE_MEMORY_CONTEXT", nextModel.LastInput);
        Assert.Contains(preference, nextModel.LastInput);
        Assert.Equal(1, nextModel.Calls);
        Assert.Equal(4, backend.Records.Count);
        Assert.Single(backend.Records.Values, fields => fields.GetProperty("Content").GetString() == preference);
        Assert.DoesNotContain(backend.Records.Values, fields => fields.GetProperty("Content").GetString()!.Contains("NATIVE_MEMORY_CONTEXT", StringComparison.Ordinal));

        using var otherProvider = CreateProvider(nextStore, "bob", "other-session");
        using var otherModel = new NativeContextChatClient();
        var otherAgent = otherModel.AsAIAgent(new ChatClientAgentOptions { AIContextProviders = [otherProvider] });
        await otherAgent.RunAsync("Which language should you use?", cancellationToken: timeout.Token);
        Assert.DoesNotContain("NATIVE_MEMORY_CONTEXT", otherModel.LastInput);
        Assert.DoesNotContain(preference, otherModel.LastInput);
    }

    [Fact]
    public async Task MicrosoftProvider_OnDemandMode_ExposesAndExecutesItsOwnSearchTool()
    {
        const string preference = "I prefer code examples in F#.";
        var backend = new NativeHistoryBackend();
        using var harness = new TestHarness(backend.Respond);
        using var embeddings = new BatchEmbeddingGenerator();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = embeddings });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var provider = CreateProvider(store, "alice", "first-session"))
        using (var model = new NativeContextChatClient())
        {
            var agent = model.AsAIAgent(new ChatClientAgentOptions { AIContextProviders = [provider] });
            await agent.RunAsync(preference, cancellationToken: timeout.Token);
        }
        using var onDemand = CreateProvider(store, "alice", "next-session", true);
        using var toolModel = new MemoryToolCallingChatClient("recall_history", "userQuestion", "programming language");
        var nextAgent = toolModel.AsAIAgent(new ChatClientAgentOptions { AIContextProviders = [onDemand] });
        var response = await nextAgent.RunAsync("What are my preferences?", cancellationToken: timeout.Token);
        Assert.Contains(preference, response.Text);
        Assert.DoesNotContain(preference, toolModel.InitialMessages);
        Assert.Equal(2, toolModel.Calls);
    }

    [Fact]
    public async Task TextSearchProvider_CoexistsWithSessionHistoryAndSerialization()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit()));
        using var embeddings = new BatchEmbeddingGenerator();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = embeddings });
        using var collection = store.GetCollection<string, Article>("article");
        var provider = new TextSearchProvider(async (query, ct) =>
        {
            List<TextSearchProvider.TextSearchResult> results = [];
            await foreach (var hit in collection.SearchAsync(query, 1, cancellationToken: ct))
                results.Add(new() { Text = hit.Record.Title, SourceName = hit.Record.Id });
            return results;
        });
        using var model = new NativeContextChatClient();
        var agent = model.AsAIAgent(new ChatClientAgentOptions { AIContextProviders = [provider] });
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("My project is named Audit.", session);
        await agent.RunAsync("Continue the project.", session);
        Assert.Contains("Vespa vector search", model.LastInput);
        Assert.Contains("My project is named Audit.", model.LastInput);
        var saved = await agent.SerializeSessionAsync(session);
        var resumed = await agent.DeserializeSessionAsync(saved);
        await agent.RunAsync("Continue after restoring the session.", resumed);
        Assert.Contains("My project is named Audit.", model.LastInput);
        Assert.Equal(3, model.Calls);
        Assert.Equal(3, harness.Handler.Requests.Count);
    }

    internal static ChatHistoryMemoryProvider CreateProvider(VespaVectorStore store, string user, string session,
        bool onDemand = false) => new(store, "chathistory", 3,
            _ => new ChatHistoryMemoryProvider.State(
                new() { ApplicationId = "test-app", AgentId = "test-agent", UserId = user, SessionId = session },
                new() { ApplicationId = "test-app", AgentId = "test-agent", UserId = user }),
            new()
            {
                ContextPrompt = "NATIVE_MEMORY_CONTEXT",
                MaxResults = 10,
                SearchTime = onDemand ? ChatHistoryMemoryProviderOptions.SearchBehavior.OnDemandFunctionCalling
                    : ChatHistoryMemoryProviderOptions.SearchBehavior.BeforeAIInvoke,
                FunctionToolName = "recall_history"
            });

    [Fact]
    public async Task TextSearchProvider_OnDemand_UsesItsOwnToolWithVespa()
    {
        using var harness = new TestHarness((_, _) => TestHarness.Json(TestHarness.Hit()));
        using var embeddings = new BatchEmbeddingGenerator();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = embeddings });
        using var collection = store.GetCollection<string, Article>("article");
        var provider = new TextSearchProvider(async (query, ct) =>
        {
            List<TextSearchProvider.TextSearchResult> results = [];
            await foreach (var hit in collection.SearchAsync(query, 1, cancellationToken: ct))
                results.Add(new() { Text = hit.Record.Title, SourceName = hit.Record.Id });
            return results;
        }, new()
        {
            SearchTime = TextSearchProviderOptions.TextSearchBehavior.OnDemandFunctionCalling,
            FunctionToolName = "search_docs"
        });
        using var model = new MemoryToolCallingChatClient("search_docs", "userQuestion", "Vespa vectors");
        var agent = model.AsAIAgent(new ChatClientAgentOptions { AIContextProviders = [provider] });
        var response = await agent.RunAsync("Search the documentation.");
        Assert.Equal(2, model.Calls);
        Assert.Contains("Vespa vector search", response.Text);
        Assert.Single(harness.Handler.Requests);
    }
}

internal sealed class NativeContextChatClient : IChatClient
{
    internal int Calls { get; private set; }
    internal string LastInput { get; private set; } = "";
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LastInput = string.Join("\n", messages.Select(m => m.Text));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Acknowledged.")));
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}

file sealed class NativeHistoryBackend
{
    internal Dictionary<string, JsonElement> Records { get; } = [];
    internal int SchemaChecks { get; private set; }
    internal HttpResponseMessage Respond(HttpRequestMessage request, string? body)
    {
        using var document = JsonDocument.Parse(body!);
        if (request.RequestUri!.AbsolutePath.StartsWith("/document/v1/", StringComparison.Ordinal))
        {
            var key = Uri.UnescapeDataString(request.RequestUri.AbsolutePath.Split("/docid/", 2)[1]);
            Records[key] = document.RootElement.GetProperty("fields").Clone();
            return TestHarness.Json("{}");
        }
        var query = document.RootElement.GetProperty("yql").GetString()!;
        var top = document.RootElement.GetProperty("hits").GetInt32();
        if (top == 0)
            SchemaChecks++;
        var rows = Records.Where(pair => new[] { "ApplicationId", "AgentId", "UserId", "SessionId" }.All(name =>
            !query.Contains(name + " contains", StringComparison.Ordinal)
            || query.Contains(JsonSerializer.Serialize(pair.Value.GetProperty(name).GetString()), StringComparison.Ordinal)))
            .Take(top).Select(pair => new
            {
                id = "index:content/0/memory",
                relevance = 1.0,
                fields = pair.Value.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value)
                    .Concat(new Dictionary<string, object>
                    {
                        ["documentid"] = $"id:test:chathistory::{pair.Key}",
                        ["matchfeatures"] = new Dictionary<string, double> { ["distance(field,ContentEmbedding)"] = 0 }
                    }).ToDictionary(p => p.Key, p => p.Value)
            }).ToArray();
        return TestHarness.Json(JsonSerializer.Serialize(new { root = new { children = rows } }));
    }
}
