#pragma warning disable MAAI001 // These tests exercise Microsoft's experimental file/session/compaction contracts.
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Vespa.Models;
using Xunit;

namespace Vespa.AgentFramework.Tests;

public sealed class PatternTests
{
    [Fact]
    public async Task Snapshots_RejectStaleWritesAndSeparateOpaqueScopes()
    {
        using var fixture = new StateFixture();
        var original = await fixture.Store.WriteAsync("same/key", JsonSerializer.SerializeToElement("original"), null);
        var next = await fixture.Store.WriteAsync("same/key", JsonSerializer.SerializeToElement("next"), original.Revision);
        await Assert.ThrowsAsync<VespaConditionNotMetException>(() => fixture.Store.WriteAsync("same/key", original.Payload, original.Revision));
        await Assert.ThrowsAsync<VespaConditionNotMetException>(() => fixture.Store.WriteAsync("same/key", original.Payload, null));
        Assert.Equal("next", (await fixture.Store.ReadAsync("same/key"))!.Payload.GetString());
        Assert.Null(await fixture.OtherScope.ReadAsync("same/key"));
        await fixture.Store.DeleteAsync("same/key", next.Revision);
        Assert.Null(await fixture.Store.ReadAsync("same/key"));
    }

    [Fact]
    public async Task History_PreservesOrderAndToolPairsAcrossNewProvidersAndSessionSerialization()
    {
        using var fixture = new StateFixture();
        var history = new VespaChatHistoryProvider(fixture.Store, _ => new() { ConversationId = "conversation-1" });
        using var model = new PatternChatClient();
        var agent = model.AsAIAgent(new ChatClientAgentOptions { Name = "test", ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync();
        ChatMessage[] input =
        [
            new(ChatRole.User, "Please run the tool."),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "calculate", new Dictionary<string, object?> { ["number"] = 3 })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "result-3")])
        ];
        await history.InvokingAsync(new(agent, session, input));
        await history.InvokedAsync(new(agent, session, input, [new(ChatRole.Assistant, "First response.")]));
        var serialized = await agent.SerializeSessionAsync(session);

        var nextHistory = new VespaChatHistoryProvider(fixture.Store);
        var nextAgent = model.AsAIAgent(new ChatClientAgentOptions { Name = "test", ChatHistoryProvider = nextHistory });
        var restored = await nextAgent.DeserializeSessionAsync(serialized);
        await nextAgent.RunAsync("Continue.", restored);
        Assert.Equal(new[] { "Please run the tool.", "", "", "First response.", "Continue." }, model.LastMessages.Select(m => m.Text));
        Assert.Equal("calculate", Assert.IsType<FunctionCallContent>(model.LastMessages[1].Contents[0]).Name);
        Assert.Equal("call-1", Assert.IsType<FunctionResultContent>(model.LastMessages[2].Contents[0]).CallId);
        Assert.Equal(6, (await nextHistory.GetMessagesAsync(restored)).Count);
        var different = model.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "test", ChatHistoryProvider = new VespaChatHistoryProvider(fixture.OtherScope, _ => new() { ConversationId = "conversation-1" })
        });
        await different.RunAsync("Other user.", await different.CreateSessionAsync());
        Assert.Single(model.LastMessages);
    }

    [Fact]
    public async Task History_DoesNotSaveEnrichedContextOrFailedRuns()
    {
        using var fixture = new StateFixture();
        var history = new VespaChatHistoryProvider(fixture.Store);
        using var model = new PatternChatClient();
        var agent = model.AsAIAgent(new ChatClientAgentOptions { Name = "test", ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync();
        await history.InvokingAsync(new(agent, session, []));
        var context = new ChatMessage(ChatRole.User, "Retrieved context.")
            .WithAgentRequestMessageSource(AgentRequestMessageSourceType.AIContextProvider, "retrieval");
        await history.InvokedAsync(new(agent, session, [new(ChatRole.User, "Question."), context], [new(ChatRole.Assistant, "Answer.")]));
        Assert.Equal(2, (await history.GetMessagesAsync(session)).Count);
        await history.InvokedAsync(new(agent, session, [new(ChatRole.User, "Failed.")], new InvalidOperationException("Model failed.")));
        Assert.Equal(2, (await history.GetMessagesAsync(session)).Count);
    }

    [Fact]
    public async Task ConcurrentHistoryRuns_RejectAStaleBranchInsteadOfOverwriting()
    {
        using var fixture = new StateFixture();
        var history = new VespaChatHistoryProvider(fixture.Store, _ => new() { ConversationId = "shared" });
        using var model = new PatternChatClient();
        var agent = model.AsAIAgent(new ChatClientAgentOptions { Name = "test", ChatHistoryProvider = history });
        var first = await agent.CreateSessionAsync();
        var second = await agent.CreateSessionAsync();
        await history.InvokingAsync(new(agent, first, []));
        await history.InvokingAsync(new(agent, second, []));
        await history.InvokedAsync(new(agent, first, [new(ChatRole.User, "First branch.")], [new(ChatRole.Assistant, "Saved.")]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => history.InvokedAsync(
            new(agent, second, [new(ChatRole.User, "Stale branch.")], [new(ChatRole.Assistant, "Must not overwrite.")])).AsTask());
        Assert.DoesNotContain(await history.GetMessagesAsync(first), m => m.Text.Contains("Stale", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostedSessionStore_RestoresIndependentSessionsAndHonorsAllPartitions()
    {
        using var fixture = new StateFixture();
        using var model = new PatternChatClient();
        var agent = model.AsAIAgent(name: "stable-agent");
        var store = new VespaAgentSessionStore(fixture.Store);
        var key = new AgentSessionStoreKey("session-1", new Dictionary<string, string> { ["tenant"] = "one", ["user"] = "alice" });
        var session = await store.GetOrCreateSessionAsync(agent, key);
        await agent.RunAsync("My project is Apollo.", session);
        await store.SaveSessionAsync(agent, key, session);
        var nextStore = new VespaAgentSessionStore(fixture.Store);
        var restored = await nextStore.GetSessionAsync(agent, key);
        var branch = await nextStore.GetSessionAsync(agent, key);
        Assert.NotNull(restored);
        Assert.NotSame(restored, branch);
        await agent.RunAsync("Continue Apollo.", restored);
        Assert.Contains(model.LastMessages, m => m.Text == "My project is Apollo.");
        await nextStore.SaveSessionAsync(agent, key, restored);
        await Assert.ThrowsAsync<VespaConditionNotMetException>(() => nextStore.SaveSessionAsync(agent, key, branch!).AsTask());
        Assert.Null(await nextStore.GetSessionAsync(agent, key.WithPartition("user", "bob")));
        await nextStore.DeleteSessionAsync(agent, key);
        await nextStore.DeleteSessionAsync(agent, key);
        Assert.Null(await nextStore.GetSessionAsync(agent, key));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("a/../escape.md")]
    [InlineData("/root.md")]
    [InlineData("C:\\root.md")]
    public async Task FileMemory_RejectsTraversalAndAbsolutePaths(string path)
    {
        using var fixture = new StateFixture();
        var files = new VespaAgentFileStore(fixture.Store);
        await Assert.ThrowsAsync<ArgumentException>(() => files.WriteAsync(path, "invalid"));
        Assert.Empty(fixture.Backend.Documents);
    }

    [Fact]
    public async Task FileMemory_ListsSearchesAndEditsTheSamePhysicalLinesAcrossRestarts()
    {
        using var fixture = new StateFixture();
        var files = new VespaAgentFileStore(fixture.Store);
        await files.CreateDirectoryAsync("empty");
        await files.WriteAsync("alice/preferences.md", "one\r\ntarget C#\rthree\nfour");
        var next = new VespaAgentFileStore(fixture.Store);
        Assert.True(await next.FileExistsAsync("ALICE/PREFERENCES.MD"));
        var root = await next.ListChildrenAsync("");
        Assert.Equal(new[] { "alice", "empty" }, root.Select(e => e.Name));
        var hit = Assert.Single(await next.SearchAsync("alice", "target", "*.md"));
        Assert.Equal("preferences.md", hit.FileName);
        var match = Assert.Single(hit.MatchingLines);
        Assert.Equal(2, match.LineNumber);
        var content = await next.ReadAsync("alice/" + hit.FileName);
        var lines = AgentFileStore.SplitLines(content!).ToList();
        Assert.Equal(match.Line, lines[match.LineNumber - 1]);
        lines[match.LineNumber - 1] = "changed F#\r";
        await next.WriteAsync("alice/" + hit.FileName, string.Concat(lines));
        Assert.Contains("changed F#", await next.ReadAsync("alice/preferences.md"));
        Assert.Empty(await new VespaAgentFileStore(fixture.OtherScope).ListChildrenAsync(""));
        Assert.True(await next.DeleteAsync("alice/preferences.md"));
        Assert.False(await next.DeleteAsync("alice/preferences.md"));
    }

    [Fact]
    public async Task NativeFileMemoryProvider_UsesVespaThroughItsOwnTools()
    {
        using var fixture = new StateFixture();
        var files = new VespaAgentFileStore(fixture.Store);
        using (var provider = new FileMemoryProvider(files, _ => new() { WorkingFolder = "alice" }))
        using (var model = new PatternChatClient("file_memory_write", new() { ["fileName"] = "preferences.md", ["content"] = "Use C# examples." }))
        {
            var agent = model.AsAIAgent(new ChatClientAgentOptions { Name = "test", AIContextProviders = [provider] });
            await agent.RunAsync("Remember my preference.");
        }
        Assert.Equal("Use C# examples.", await files.ReadAsync("alice/preferences.md"));
        using var nextProvider = new FileMemoryProvider(new VespaAgentFileStore(fixture.Store), _ => new() { WorkingFolder = "alice" });
        using var nextModel = new PatternChatClient("file_memory_read", new() { ["fileName"] = "preferences.md" });
        var nextAgent = nextModel.AsAIAgent(new ChatClientAgentOptions { Name = "test", AIContextProviders = [nextProvider] });
        var result = await nextAgent.RunAsync("Read my preference.");
        Assert.Contains("Use C# examples.", result.Text);
    }

    [Fact]
    public async Task Compaction_OnTheChatBuilder_PreservesTheStoredArchive()
    {
        using var fixture = new StateFixture();
        var history = new VespaChatHistoryProvider(fixture.Store);
        using var model = new PatternChatClient();
        using var compacted = model.AsBuilder().UseAIContextProviders(new CompactionProvider(
            new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(1)))).Build();
        var agent = compacted.AsAIAgent(new ChatClientAgentOptions { Name = "test", ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("First turn.", session);
        await agent.RunAsync("Second turn.", session);
        await agent.RunAsync("Third turn.", session);
        Assert.DoesNotContain(model.LastMessages, m => m.Text == "First turn.");
        Assert.Equal(6, (await history.GetMessagesAsync(session)).Count);
    }

    [Theory]
    [InlineData(VespaHistoryReductionEvent.BeforeMessagesRetrieval)]
    [InlineData(VespaHistoryReductionEvent.AfterMessagesAdded)]
    public async Task HistoryReducer_PersistsReductionAtTheSelectedEvent(VespaHistoryReductionEvent timing)
    {
        using var fixture = new StateFixture();
        var history = new VespaChatHistoryProvider(fixture.Store, options: new()
        {
            ChatReducer = new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(1)).AsChatReducer(),
            ReducerTriggerEvent = timing
        });
        using var model = new PatternChatClient();
        var agent = model.AsAIAgent(new ChatClientAgentOptions { Name = "test", ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("First turn.", session);
        await agent.RunAsync("Second turn.", session);
        await agent.RunAsync("Third turn.", session);
        var stored = await history.GetMessagesAsync(session);
        Assert.DoesNotContain(stored, m => m.Text == "First turn.");
        Assert.Equal(timing == VespaHistoryReductionEvent.AfterMessagesAdded ? 2 : 4, stored.Count);
    }

    [Fact]
    public async Task PayloadLimitsAndCancellation_FailBeforeWriting()
    {
        using var fixture = new StateFixture();
        var bounded = new VespaAgentStateStore(fixture.Client, new() { Namespace = "test", Scope = "alice", MaxPayloadBytes = 10 });
        await Assert.ThrowsAsync<ArgumentException>(() => bounded.WriteAsync("key", JsonSerializer.SerializeToElement(new string('x', 20)), null));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bounded.ReadAsync("key", cancelled.Token));
        Assert.Empty(fixture.Backend.Documents);
    }

    [Fact]
    public async Task Harness_UsesVespaHistoryAndFileMemoryInsideItsToolLoop()
    {
        using var fixture = new StateFixture();
        var history = new VespaChatHistoryProvider(fixture.Store);
        var files = new VespaAgentFileStore(fixture.Store);
        using var model = new PatternChatClient("file_memory_write", new() { ["fileName"] = "note.md", ["content"] = "Harness memory." });
        var harness = model.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = "harness",
            ChatHistoryProvider = history,
            FileMemoryStore = files,
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true,
            MaxContextWindowTokens = 16000,
            MaxOutputTokens = 2000
        });
        var session = await harness.CreateSessionAsync();
        await harness.RunAsync("Save the note.", session);
        var messages = await history.GetMessagesAsync(session);
        Assert.Contains(messages.SelectMany(m => m.Contents), c => c is FunctionCallContent { Name: "file_memory_write" });
        Assert.Contains(messages.SelectMany(m => m.Contents), c => c is FunctionResultContent);
        Assert.DoesNotContain(messages, m => m.Text.Contains("## File Based Memory", StringComparison.Ordinal));
        Assert.Contains(await files.SearchAsync("", "Harness memory", recursive: true), f => f.FileName.EndsWith("note.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostedSessionStore_PreservesRemoteConversationIdsAndProviderState()
    {
        using var fixture = new StateFixture();
        using var model = new PatternChatClient();
        var agent = model.AsAIAgent(name: "remote-agent");
        var session = await agent.CreateSessionAsync("remote-conversation-123");
        session.StateBag.SetValue("custom-state", new Dictionary<string, string> { ["value"] = "preserved" });
        var store = new VespaAgentSessionStore(fixture.Store);
        var key = new AgentSessionStoreKey("remote");
        await store.SaveSessionAsync(agent, key, session);
        var resumed = Assert.IsType<ChatClientAgentSession>(await new VespaAgentSessionStore(fixture.Store).GetSessionAsync(agent, key));
        Assert.Equal("remote-conversation-123", resumed.ConversationId);
        Assert.Equal("preserved", resumed.StateBag.GetValue<Dictionary<string, string>>("custom-state")!["value"]);
    }

    [Fact]
    public async Task StreamingRun_PersistsHistoryAfterSuccessfulCompletion()
    {
        using var fixture = new StateFixture();
        using var model = new PatternChatClient();
        var history = new VespaChatHistoryProvider(fixture.Store);
        var agent = model.AsAIAgent(new ChatClientAgentOptions { Name = "streaming", ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync();
        await foreach (var update in agent.RunStreamingAsync("Stream this response.", session))
            Assert.NotNull(update);
        Assert.Equal(2, (await history.GetMessagesAsync(session)).Count);
    }

    [Fact]
    public async Task History_RoundTripsSerializableMediaAndMessageMetadata()
    {
        using var fixture = new StateFixture();
        using var model = new PatternChatClient();
        var history = new VespaChatHistoryProvider(fixture.Store);
        var agent = model.AsAIAgent(new ChatClientAgentOptions { Name = "media", ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync();
        var input = new ChatMessage(ChatRole.User,
        [
            new TextContent("Inspect this image."),
            new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
            new UriContent(new Uri("https://example.org/image.png"), "image/png")
        ]) { AuthorName = "alice", MessageId = "message-1" };
        await agent.RunAsync([input], session);
        var saved = (await history.GetMessagesAsync(session))[0];
        Assert.Equal("alice", saved.AuthorName);
        Assert.Equal("message-1", saved.MessageId);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<DataContent>(saved.Contents[1]).Data.ToArray());
        Assert.Equal("https://example.org/image.png", Assert.IsType<UriContent>(saved.Contents[2]).Uri.AbsoluteUri);
    }
}

internal sealed class StateFixture : IDisposable
{
    internal SnapshotBackend Backend { get; } = new();
    internal VespaClient Client { get; }
    internal VespaAgentStateStore Store { get; }
    internal VespaAgentStateStore OtherScope { get; }
    public StateFixture()
    {
        Client = new VespaClient(new HttpClient(Backend) { BaseAddress = new Uri("http://localhost:8080") }, new() { Endpoint = "http://localhost:8080" });
        Store = new(Client, new() { Namespace = "test", Scope = "app/agent/alice" });
        OtherScope = new(Client, new() { Namespace = "test", Scope = "app/agent/bob" });
    }
    public void Dispose() => Client.Dispose();
}

internal sealed class SnapshotBackend : HttpMessageHandler
{
    internal Dictionary<string, JsonElement> Documents { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var key = uri.AbsolutePath;
        if (key == "/search/")
        {
            using var queryBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var rows = Documents.Where(pair => pair.Key.Contains("/memory/docid/", StringComparison.Ordinal)).Select(pair => new
            {
                id = "index:content/0/memory",
                fields = pair.Value.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value)
                    .Concat(new Dictionary<string, object>
                    {
                        ["documentid"] = "id:knowledge:memory::" + Uri.UnescapeDataString(pair.Key.Split("/docid/", 2)[1]),
                        ["matchfeatures"] = new Dictionary<string, double> { ["distance(field,embedding)"] = 0 }
                    }).ToDictionary(p => p.Key, p => p.Value)
            });
            return Json(new { root = new { children = rows } });
        }
        var exists = Documents.TryGetValue(key, out var fields);
        if (request.Method == HttpMethod.Get)
            return exists ? Json(new { fields }) : new(HttpStatusCode.NotFound);
        var query = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        var condition = query.GetValueOrDefault("condition");
        var expected = condition?.Split('"')[1];
        if (condition is not null && !(exists ? fields.GetProperty("revision").GetString() == expected : query.GetValueOrDefault("create") == "true"))
            return new(HttpStatusCode.PreconditionFailed) { Content = new StringContent("{\"message\":\"Condition not met\"}") };
        if (request.Method == HttpMethod.Delete)
            Documents.Remove(key);
        else
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Documents[key] = body.RootElement.GetProperty("fields").Clone();
        }
        return Json(new { });
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };
}

internal sealed class PatternChatClient(string? toolName = null, Dictionary<string, object?>? arguments = null) : IChatClient
{
    internal List<ChatMessage> LastMessages { get; private set; } = [];
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastMessages = messages.ToList();
        if (toolName is not null && LastMessages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault() is not { } result)
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", toolName, arguments)])));
        var response = LastMessages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault()?.Result?.ToString() ?? "Acknowledged.";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }
    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
