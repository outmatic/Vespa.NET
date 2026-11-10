#pragma warning disable MAAI001 // Microsoft session stores, file stores and compaction are experimental.
using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Vespa.AgentFramework;
using Vespa.VectorData;

namespace Vespa.AgentFrameworkSample;

internal static class ConversationPatterns
{
    internal static async Task RunAsync(string pattern, string? compaction, bool reduceHistory, bool onDemand,
        bool streaming, string question, IVespaClient client, IChatClient chat, IEmbeddingGenerator<string, Embedding<float>> embeddings,
        VespaVectorStore vectors, CancellationToken cancellationToken)
    {
        var user = Environment.GetEnvironmentVariable("VESPA_MEMORY_USER") ?? "demo-user";
        var conversation = Environment.GetEnvironmentVariable("VESPA_CONVERSATION_ID") ?? "demo-conversation";
        var snapshots = new VespaAgentStateStore(client, new()
        {
            Namespace = "knowledge",
            Scope = System.Text.Json.JsonSerializer.Serialize(new[] { "vespa-sample", pattern, user })
        });
        var strategy = CreateStrategy(compaction ?? "sliding", chat);
        var history = new VespaChatHistoryProvider(snapshots, _ => new() { ConversationId = conversation },
            new() { ChatReducer = reduceHistory ? strategy.AsChatReducer() : null });
        using var preferences = vectors.GetCollection<string, MemoryRecord>("memory");
        using var articles = vectors.GetCollection<string, Article>("article");
        using var files = new FileMemoryProvider(new VespaAgentFileStore(snapshots));
        var options = new ChatClientAgentOptions
        {
            Name = "VespaPatterns",
            ChatOptions = new() { Instructions = "Answer using the conversation and available context. Use memory tools when asked to remember or recall information." },
            ChatHistoryProvider = pattern == "session" ? new InMemoryChatHistoryProvider(new()
            {
                ChatReducer = reduceHistory ? strategy.AsChatReducer() : null
            }) : history
        };
        AIAgent agent;
        switch (pattern)
        {
            case "session":
            case "history":
                agent = chat.AsAIAgent(options);
                break;
            case "context":
                options.AIContextProviders = [new PreferenceContextProvider(preferences, embeddings, _ => new() { UserId = user })];
                agent = chat.AsAIAgent(options);
                break;
            case "text-search":
                options.AIContextProviders = [new TextSearchProvider(async (query, ct) =>
                {
                    List<TextSearchProvider.TextSearchResult> results = [];
                    await foreach (var hit in articles.SearchAsync("search_query: " + query, 3,
                        new() { Filter = a => a.Published && a.Category == "agent-framework" }, ct))
                        results.Add(new() { Text = hit.Record.Title, SourceName = hit.Record.Id });
                    return results;
                }, new()
                {
                    SearchTime = onDemand ? TextSearchProviderOptions.TextSearchBehavior.OnDemandFunctionCalling
                        : TextSearchProviderOptions.TextSearchBehavior.BeforeAIInvoke
                })];
                agent = chat.AsAIAgent(options);
                break;
            case "file-memory":
                options.AIContextProviders = [files];
                agent = chat.AsAIAgent(options);
                break;
            case "compaction":
                // Builder registration compacts model requests while the history provider keeps the archive.
                agent = chat.AsBuilder().UseAIContextProviders(new CompactionProvider(strategy)).BuildAIAgent(options);
                break;
            case "harness":
                agent = chat.AsHarnessAgent(new HarnessAgentOptions
                {
                    Name = "VespaPatterns",
                    ChatHistoryProvider = history,
                    FileMemoryStore = new VespaAgentFileStore(snapshots),
                    CompactionStrategy = strategy,
                    DisableAgentSkillsProvider = true,
                    DisableWebSearch = true
                });
                break;
            default:
                throw new ArgumentException("Choose session, history, context, text-search, file-memory, compaction or harness.", nameof(pattern));
        }
        var sessions = new VespaAgentSessionStore(snapshots);
        var key = new AgentSessionStoreKey(conversation);
        var session = await sessions.GetOrCreateSessionAsync(agent, key, cancellationToken);
        if (streaming)
        {
            await foreach (var update in agent.RunStreamingAsync(question, session, cancellationToken: cancellationToken))
                Console.Write(update.Text);
            Console.WriteLine();
        }
        else
            Console.WriteLine(await agent.RunAsync(question, session, cancellationToken: cancellationToken));
        await sessions.SaveSessionAsync(agent, key, session, cancellationToken);
        Console.WriteLine($"Saved conversation '{conversation}' for user '{user}'.");
    }

    internal static CompactionStrategy CreateStrategy(string name, IChatClient summarizer) => name switch
    {
        "sliding" => new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(4)),
        "truncation" => new TruncationCompactionStrategy(CompactionTriggers.TokensExceed(8000), minimumPreservedGroups: 4),
        "tools" => new ToolResultCompactionStrategy(CompactionTriggers.TokensExceed(2000)),
        "summary" => new SummarizationCompactionStrategy(summarizer, CompactionTriggers.TurnsExceed(4)),
        "pipeline" => new PipelineCompactionStrategy(
            new ToolResultCompactionStrategy(CompactionTriggers.TokensExceed(2000)),
            new SummarizationCompactionStrategy(summarizer, CompactionTriggers.TokensExceed(4000)),
            new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(4)),
            new TruncationCompactionStrategy(CompactionTriggers.TokensExceed(8000), minimumPreservedGroups: 4)),
        _ => throw new ArgumentException("Choose sliding, truncation, tools, summary or pipeline.", nameof(name))
    };
}

internal sealed class PreferenceContextProvider : AIContextProvider
{
    private readonly VectorStoreCollection<string, MemoryRecord> _memories;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly ProviderSessionState<State> _state;

    internal PreferenceContextProvider(VectorStoreCollection<string, MemoryRecord> memories,
        IEmbeddingGenerator<string, Embedding<float>> embeddings, Func<AgentSession?, State> initializer)
    {
        _memories = memories;
        _embeddings = embeddings;
        _state = new(initializer, nameof(PreferenceContextProvider));
    }

    public override IReadOnlyList<string> StateKeys => [_state.StateKey];

    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var state = _state.GetOrInitializeState(context.Session);
        var memory = new PreferenceMemory(_memories, _embeddings, state.UserId);
        var query = string.Join("\n", context.AIContext.Messages?.Select(m => m.Text) ?? []);
        var recalled = await memory.RecallAsync(query, cancellationToken);
        return new()
        {
            Messages = string.IsNullOrEmpty(recalled) ? [] : [new(ChatRole.User, "Stored code-example preference: " + recalled)],
            Tools = [AIFunctionFactory.Create(
                [Description("Save the user's current programming-language preference for code examples.")]
                async (string preference, CancellationToken ct) => await memory.RememberAsync(preference, ct),
                "remember_preference")]
        };
    }

    protected override ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _state.GetOrInitializeState(context.Session);
        state.CompletedRuns++;
        _state.SaveState(context.Session, state);
        return ValueTask.CompletedTask;
    }

    internal sealed class State
    {
        public string UserId { get; set; } = "";
        public int CompletedRuns { get; set; }
    }
}
