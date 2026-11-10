using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Vespa.AgentFramework;

/// <summary>Ordered conversation history, separate from semantic memory and from serialized sessions.</summary>
public sealed class VespaChatHistoryProvider : ChatHistoryProvider
{
    private readonly VespaAgentStateStore _store;
    private readonly VespaChatHistoryProviderOptions _options;
    private readonly ProviderSessionState<State> _state;

    public VespaChatHistoryProvider(VespaAgentStateStore store, Func<AgentSession?, State>? stateInitializer = null,
        VespaChatHistoryProviderOptions? options = null)
        : base(storeInputRequestMessageFilter: messages => messages.Where(m =>
            m.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External))
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _options = options ?? new();
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.StateKey);
        _state = new(stateInitializer ?? (_ => new()), _options.StateKey);
    }

    public override IReadOnlyList<string> StateKeys => [_state.StateKey];

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var state = _state.GetOrInitializeState(session);
        return Messages(await _store.ReadAsync(Key(state), cancellationToken));
    }

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var state = _state.GetOrInitializeState(context.Session);
        var snapshot = await _store.ReadAsync(Key(state), cancellationToken);
        var messages = Messages(snapshot);
        if (_options.ChatReducer is { } reducer && _options.ReducerTriggerEvent == VespaHistoryReductionEvent.BeforeMessagesRetrieval)
        {
            messages = (await reducer.ReduceAsync(messages, cancellationToken)).ToList();
            if (snapshot is not null)
                snapshot = await _store.WriteAsync(Key(state), JsonSerializer.SerializeToElement(messages), snapshot.Revision, cancellationToken);
        }
        state.Revision = snapshot?.Revision;
        _state.SaveState(context.Session, state);
        return messages;
    }

    protected override async ValueTask StoreChatHistoryAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        var state = _state.GetOrInitializeState(context.Session);
        var snapshot = await _store.ReadAsync(Key(state), cancellationToken);
        if (snapshot?.Revision != state.Revision)
            throw new InvalidOperationException("Conversation history changed during this run. Reload the conversation and rerun before saving.");
        var messages = Messages(snapshot);
        messages.AddRange(context.RequestMessages.Concat(context.ResponseMessages ?? []));
        if (_options.ChatReducer is { } reducer && _options.ReducerTriggerEvent == VespaHistoryReductionEvent.AfterMessagesAdded)
            messages = (await reducer.ReduceAsync(messages, cancellationToken)).ToList();
        var saved = await _store.WriteAsync(Key(state), JsonSerializer.SerializeToElement(messages), state.Revision, cancellationToken);
        state.Revision = saved.Revision;
        _state.SaveState(context.Session, state);
    }

    private static List<ChatMessage> Messages(VespaAgentStateSnapshot? snapshot) => snapshot is null ? []
        : snapshot.Payload.Deserialize<List<ChatMessage>>() ?? throw new JsonException("The history snapshot is missing its messages.");

    private static string Key(State state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state.ConversationId);
        return "history/" + state.ConversationId;
    }

    public sealed class State
    {
        public string ConversationId { get; set; } = Guid.NewGuid().ToString("D");
        public string? Revision { get; set; }
    }
}

public sealed record VespaChatHistoryProviderOptions
{
    public string StateKey { get; init; } = nameof(VespaChatHistoryProvider);
    public IChatReducer? ChatReducer { get; init; }
    public VespaHistoryReductionEvent ReducerTriggerEvent { get; init; } = VespaHistoryReductionEvent.BeforeMessagesRetrieval;
}

public enum VespaHistoryReductionEvent
{
    BeforeMessagesRetrieval,
    AfterMessagesAdded
}
