#pragma warning disable MAAI001 // Exercises published session, history and Harness contracts.
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Vespa.AgentFramework.Tests;

public sealed class HistoryReliabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeforeRetrievalReducer_SavesOnlyChangedContentIncludingInPlaceEdits(bool mutate)
    {
        using var fixture = new StateFixture();
        var original = await fixture.Store.WriteAsync("history/conversation", JsonSerializer.SerializeToElement(
            new[] { new ChatMessage(ChatRole.User, "Original.") }), null);
        var writes = fixture.Backend.PutCount;
        var history = new VespaChatHistoryProvider(fixture.Store, _ => new() { ConversationId = "conversation" },
            new() { ChatReducer = new EditingReducer(mutate) });
        using var model = new PatternChatClient();
        var agent = model.AsAIAgent(new ChatClientAgentOptions { Name = "reduction", ChatHistoryProvider = history });
        var session = await agent.CreateSessionAsync();

        await history.InvokingAsync(new(agent, session, []));

        var saved = (await fixture.Store.ReadAsync("history/conversation"))!;
        Assert.Equal(writes + (mutate ? 1 : 0), fixture.Backend.PutCount);
        Assert.Equal(mutate ? "Edited." : "Original.", Assert.Single(await history.GetMessagesAsync(session)).Text);
        Assert.Equal(!mutate, original.Revision == saved.Revision);
        Assert.Equal(saved.Revision, session.StateBag.GetValue<VespaChatHistoryProvider.State>(nameof(VespaChatHistoryProvider))!.Revision);
    }

    [Fact]
    public async Task Harness_FailedSecondModelCallLeavesPartialHistoryAndOlderSessionSnapshot()
    {
        using var fixture = new StateFixture();
        var history = new VespaChatHistoryProvider(fixture.Store, _ => new() { ConversationId = "partial" });
        var files = new VespaAgentFileStore(fixture.Store);
        using var model = new FailingSecondCallClient();
        var harness = model.AsHarnessAgent(new HarnessAgentOptions
        {
            Name = "partial-harness",
            ChatHistoryProvider = history,
            FileMemoryStore = files,
            AIContextProviders = [new SuccessfulCallCounter()],
            DisableAgentSkillsProvider = true,
            DisableWebSearch = true
        });
        var sessions = new VespaAgentSessionStore(fixture.Store);
        var key = new AgentSessionStoreKey("partial");
        var session = await harness.CreateSessionAsync();
        session.StateBag.SetValue("successful-calls", new CounterState());
        await sessions.SaveSessionAsync(harness, key, session);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync("Save a note.", session));

        Assert.Equal(2, model.CallCount);
        var saved = await history.GetMessagesAsync(session);
        Assert.Equal(2, saved.Count);
        Assert.Equal("Save a note.", saved[0].Text);
        Assert.Contains(saved.SelectMany(m => m.Contents), c => c is FunctionCallContent { CallId: "call-1" });
        Assert.DoesNotContain(saved.SelectMany(m => m.Contents), c => c is FunctionResultContent);
        Assert.Contains(model.LastMessages.SelectMany(m => m.Contents), c => c is FunctionResultContent { CallId: "call-1" });
        var note = Assert.Single(await files.SearchAsync("", "^Saved before failure\\.$", recursive: true));
        Assert.EndsWith("note.md", note.FileName);
        Assert.Equal("Saved before failure.", await files.ReadAsync(note.FileName));
        Assert.Equal(1, session.StateBag.GetValue<CounterState>("successful-calls")!.Count);

        var restored = (await sessions.GetSessionAsync(harness, key))!;
        Assert.Equal(0, restored.StateBag.GetValue<CounterState>("successful-calls")!.Count);
        Assert.Equal(2, (await history.GetMessagesAsync(restored)).Count);
    }
}

file sealed class EditingReducer(bool mutate) : IChatReducer
{
    public Task<IEnumerable<ChatMessage>> ReduceAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mutate)
            messages.First().Contents = [new TextContent("Edited.")];
        return Task.FromResult(messages);
    }
}

file sealed class SuccessfulCallCounter : AIContextProvider
{
    protected override ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        var session = context.Session!;
        var state = session.StateBag.GetValue<CounterState>("successful-calls")!;
        state.Count++;
        session.StateBag.SetValue("successful-calls", state);
        return ValueTask.CompletedTask;
    }
}

file sealed class CounterState
{
    public int Count { get; set; }
}

file sealed class FailingSecondCallClient : IChatClient
{
    internal int CallCount { get; private set; }
    internal List<ChatMessage> LastMessages { get; private set; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastMessages = messages.ToList();
        if (++CallCount > 1)
            throw new InvalidOperationException("Second model call failed.");
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("call-1", "file_memory_write", new Dictionary<string, object?>
            {
                ["fileName"] = "note.md", ["content"] = "Saved before failure."
            })])));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
