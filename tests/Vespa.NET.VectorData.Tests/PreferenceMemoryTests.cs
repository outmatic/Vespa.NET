using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Vespa.AgentFrameworkSample;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class PreferenceMemoryTests
{
    [Fact]
    public async Task RememberTool_ThenFreshAgentAndStore_RecallsPreferenceWithoutPreviousConversation()
    {
        const string preference = "The user prefers code examples in C#.";
        var backend = new MemoryBackend();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var harness = new TestHarness(backend.Respond))
        using (var embeddings = new FixedEmbeddingGenerator())
        using (var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = embeddings }))
        using (var collection = GetCollection(store))
        using (var model = new MemoryToolCallingChatClient("remember_preference", "preference", preference))
        {
            var memory = new PreferenceMemory(collection, embeddings, "alice");
            var agent = memory.CreateAgent(model);
            var response = await agent.RunAsync("Remember that I prefer code examples in C#.", cancellationToken: timeout.Token);
            Assert.Contains("Preference saved.", response.Text);
            Assert.Equal(2, model.Calls);
            Assert.Equal("search_document: " + preference, Assert.Single(embeddings.Inputs));
            Assert.Single(backend.Records);
        }

        // Recreate every client, collection, memory helper and agent. Only backend data survives.
        using var nextHarness = new TestHarness(backend.Respond);
        using var nextEmbeddings = new FixedEmbeddingGenerator();
        using var nextStore = new VespaVectorStore(nextHarness.Client, TestHarness.Options with { EmbeddingGenerator = nextEmbeddings });
        using var nextCollection = GetCollection(nextStore);
        using var nextModel = new MemoryToolCallingChatClient("recall_preferences", "query", "preferred code-example language");
        var nextMemory = new PreferenceMemory(nextCollection, nextEmbeddings, "alice");
        var nextAgent = nextMemory.CreateAgent(nextModel);
        var recalled = await nextAgent.RunAsync("Which language should you use for my examples?", cancellationToken: timeout.Token);
        Assert.Contains(preference, recalled.Text);
        Assert.Equal(2, nextModel.Calls);
        Assert.DoesNotContain(preference, nextModel.InitialMessages);
        Assert.DoesNotContain("C#", nextModel.InitialMessages);
        Assert.Equal("search_query: preferred code-example language", Assert.Single(nextEmbeddings.Inputs));
        var request = Assert.Single(nextHarness.Handler.Requests);
        using var query = JsonDocument.Parse(request.Body!);
        var yql = query.RootElement.GetProperty("yql").GetString();
        Assert.Contains("user_id contains", yql);
        Assert.Contains("\"alice\"", yql);
    }

    [Fact]
    public async Task PreferenceUpdate_ReplacesOldValue_AndRecallFiltersByApplicationUser()
    {
        var backend = new MemoryBackend();
        using var harness = new TestHarness(backend.Respond);
        using var embeddings = new FixedEmbeddingGenerator();
        using var store = new VespaVectorStore(harness.Client, TestHarness.Options with { EmbeddingGenerator = embeddings });
        using var collection = GetCollection(store);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var alice = new PreferenceMemory(collection, embeddings, "alice");
        await alice.RememberAsync("Use C# for code examples.", timeout.Token);
        await alice.RememberAsync("Use F# for code examples.", timeout.Token);

        Assert.Single(backend.Records);
        Assert.Equal("Use F# for code examples.", await alice.RecallAsync("code examples", timeout.Token));
        var bob = new PreferenceMemory(collection, embeddings, "bob");
        Assert.Equal("No saved preference found.", await bob.RecallAsync("code examples", timeout.Token));
    }

    internal static VectorStoreCollection<string, MemoryRecord> GetCollection(VespaVectorStore store) =>
        store.GetCollection<string, MemoryRecord>("memory", new()
        {
            Properties =
            [
                new VectorStoreKeyProperty(nameof(MemoryRecord.Id), typeof(string)),
                new VectorStoreDataProperty(nameof(MemoryRecord.UserId), typeof(string)) { StorageName = "user_id", IsIndexed = true },
                new VectorStoreDataProperty(nameof(MemoryRecord.Topic), typeof(string)) { StorageName = "topic", IsIndexed = true },
                new VectorStoreDataProperty(nameof(MemoryRecord.Text), typeof(string)) { StorageName = "text" },
                new VectorStoreVectorProperty(nameof(MemoryRecord.Embedding), 3)
                { StorageName = "embedding", DistanceFunction = DistanceFunction.CosineSimilarity }
            ]
        });
}

// Persist actual serialized request fields across independent client lifetimes.
file sealed class MemoryBackend
{
    internal Dictionary<string, JsonElement> Records { get; } = [];

    internal HttpResponseMessage Respond(HttpRequestMessage request, string? body)
    {
        using var json = JsonDocument.Parse(body!);
        if (request.RequestUri!.AbsolutePath.Contains("/document/v1/", StringComparison.Ordinal))
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var key = Uri.UnescapeDataString(request.RequestUri.AbsolutePath.Split("/docid/", 2)[1]);
            Records[key] = json.RootElement.GetProperty("fields").Clone();
            return TestHarness.Json("{}");
        }

        var yql = json.RootElement.GetProperty("yql").GetString()!;
        Assert.Contains("user_id contains", yql);
        Assert.Contains("topic contains", yql);
        var hits = Records.Where(pair =>
            yql.Contains(JsonSerializer.Serialize(pair.Value.GetProperty("user_id").GetString()), StringComparison.Ordinal)
            && yql.Contains("\"example-language\"", StringComparison.Ordinal))
            .Select(pair => new
            {
                id = "index:content/0/memory",
                relevance = 1.0,
                fields = new Dictionary<string, object>
                {
                    ["documentid"] = $"id:test:memory::{pair.Key}",
                    ["user_id"] = pair.Value.GetProperty("user_id"),
                    ["topic"] = pair.Value.GetProperty("topic"),
                    ["text"] = pair.Value.GetProperty("text"),
                    ["matchfeatures"] = new Dictionary<string, double> { ["distance(field,embedding)"] = 0 }
                }
            }).ToArray();
        return TestHarness.Json(JsonSerializer.Serialize(new { root = new { children = hits } }));
    }
}

internal sealed class MemoryToolCallingChatClient(string toolName, string argumentName, string argumentValue) : IChatClient
{
    internal int Calls { get; private set; }
    internal string InitialMessages { get; private set; } = "";

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var history = messages.ToArray();
        if (Calls++ == 0)
            InitialMessages = string.Join("\n", history.Select(m => m.Text));
        var result = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        var response = result is null
            ? new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-memory", toolName, new Dictionary<string, object?> { [argumentName] = argumentValue })])
            : new ChatMessage(ChatRole.Assistant, result.Result?.ToString());
        return Task.FromResult(new ChatResponse(response));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
