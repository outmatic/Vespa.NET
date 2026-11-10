# Conversations and memory with Microsoft Agent Framework

The examples target the .NET patterns in Microsoft's [conversation overview](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/),
[context providers](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/context-providers),
[storage](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/storage),
[sessions](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/session)
and [compaction](https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/compaction) guides.
Code is compiled and tested against Microsoft.Agents.AI and Microsoft.Agents.AI.Harness 1.26.0.

Two optional packages serve different parts of this integration:

- `Vespa.NET.VectorData`: typed/dictionary vector collections, retrieval and semantic memory.
- `Vespa.NET.AgentFramework`: complete session snapshots, chronological history and file memory.

The base Vespa.NET client acquires neither dependency. The sample runs on .NET 10;
both libraries target .NET 8 and .NET 10.

## Pattern map

| Microsoft pattern | Implementation | Sample |
|---|---|---|
| In-memory conversation history | Microsoft's `InMemoryChatHistoryProvider` | `--pattern session` |
| Reuse, serialize and restore `AgentSession` | `VespaAgentSessionStore`, using the agent's serializer | All `--pattern` modes |
| Hosted session persistence by continuation key | `VespaAgentSessionStore : AgentSessionStore` | `--pattern session` |
| Service-managed conversation | Keep the service conversation ID in the serialized session | Tested ID/state round-trip; requires a service-capable chat client |
| External chronological history | `VespaChatHistoryProvider : ChatHistoryProvider` | `--pattern history` |
| Per-session provider state | Microsoft's `ProviderSessionState<T>` | History and custom context modes |
| Custom context enrichment and tools | Sample `PreferenceContextProvider : AIContextProvider` | `--pattern context` |
| Post-run context-provider state | Sample `StoreAIContextAsync` records completed runs | `--pattern context` |
| Automatic text retrieval | Microsoft's `TextSearchProvider` with a Vespa search delegate | `--pattern text-search` |
| Model-selected text retrieval | `TextSearchProvider` on-demand mode | Add `--on-demand` |
| Semantic chat-history recall | Microsoft's unmodified `ChatHistoryMemoryProvider` | `--history-memory` |
| Model-selected semantic recall | `ChatHistoryMemoryProvider` on-demand mode | Add `--on-demand` |
| Explicit application memory tools | Sample `PreferenceMemory` | `--memory` |
| File memory | Microsoft's `FileMemoryProvider` over `VespaAgentFileStore` | `--pattern file-memory` |
| Context compaction inside the model/tool loop | Microsoft's `CompactionProvider` on the chat builder | `--pattern compaction` |
| Persistent history reduction | `IChatReducer` on either in-memory or Vespa history | Add `--reduce-history` |
| Harness history, file memory and compaction | Microsoft's `HarnessAgent` with Vespa stores | `--pattern harness` |

These cover the .NET conversation and memory patterns. Python/Go provider APIs,
remote service provisioning and unrelated Harness capabilities such as shell
execution and background-agent delegation are outside this integration.

## Deploy once

Start the dedicated Docker instance and Ollama models described in the
[sample README](../samples/Vespa.NET.AgentFramework/README.md), then run:

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- --deploy-only
```

The complete application includes `article` for document retrieval, `memory` for
explicit preferences, `chathistory` for semantic history and `agentstate` for JSON
snapshots. `agentstate` uses `store-only` mode; its operations need no search index
or embedding model. Deployment replaces the application's configuration on that instance.

`VespaAgentStateStore` requires Vespa's conditional-put-with-create support
(Vespa 8.178 or newer). See the official [conditional writes guide](https://docs.vespa.ai/en/writing/document-v1-api-guide.html#conditional-writes).
Neither integration package deploys an application through collection operations.

## Session snapshots

`VespaAgentSessionStore` implements Microsoft's hosted-store contract and can also
be called directly. With the default in-memory history provider, the serialized
session contains the messages and provider state needed to resume a conversation.

```csharp
#pragma warning disable MAAI001 // Microsoft's AgentSessionStoreKey is experimental.
var snapshots = new VespaAgentStateStore(client, new()
{
    Namespace = "knowledge",
    Scope = "my-app/my-agent/authenticated-user"
});
var sessions = new VespaAgentSessionStore(snapshots);
var key = new AgentSessionStoreKey("conversation-id", new Dictionary<string, string>
{
    ["tenant"] = "authenticated-tenant"
});
var agent = chatClient.AsAIAgent(name: "Assistant");
var session = await sessions.GetOrCreateSessionAsync(agent, key, ct);
Console.WriteLine(await agent.RunAsync("Continue our conversation.", session, cancellationToken: ct));
await sessions.SaveSessionAsync(agent, key, session, ct);
```

All key partitions are included in identity, in a stable order. The agent must
have a stable, nonempty `Name`. A successful lookup returns a new session instance.
Different callers do not share mutable session objects.

For manual persistence, the equivalent framework operations are
`await agent.SerializeSessionAsync(session)` and
`await agent.DeserializeSessionAsync(payload)`. Some Learn snippets use the older
`SerializeSession` spelling; the published version used here exposes the async API.

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern session "My project is Apollo."
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern session "What is my project's name?"
```

The sample uses `VESPA_CONVERSATION_ID` (default `demo-conversation`) and
`VESPA_MEMORY_USER` (default `demo-user`). Pattern names have separate storage
scopes, so switching modes does not reinterpret an earlier mode's state.

### Caveats

- A loaded session must be saved through the same store instance that loaded it;
  that instance tracks the revision of each session object. Create a new store on
  another process, load there, then save there. A manually deserialized session
  has no stored revision and can only create a new key.
- Writes use optimistic concurrency. Saving two branches loaded from the same
  revision rejects the stale branch instead of overwriting the first. Serialize
  runs per conversation, or handle conflicts by reloading and rerunning the agent.
  Retrying only the final save cannot reconcile an answer based on stale context.
- The framework's hosted contract has no revision parameter for deletes. Delete
  reads the latest revision and conditionally removes it. Coordinate deletion with
  active requests; it is not a transaction with an agent invocation.
- Deleting a session snapshot does not delete external chat history, semantic
  memories or files. Apply deletion and retention to each store used by that agent.
- A session containing a service-managed conversation ID resumes that remote
  conversation only while the service still retains it and credentials permit
  access. Vespa stores the ID and provider state, not the service's transcript.
  Use the service's storage settings and retention rules for that conversation.
- Register this implementation as the host's `AgentSessionStore` according to the
  host's DI setup. The sample demonstrates the store contract directly; it does
  not launch an HTTP hosting stack.

## Chronological history

`VespaChatHistoryProvider` stores the ordered message list outside the session.
The session carries its conversation ID and revision. Use a state initializer to
choose an authorized existing conversation, or let the provider create an ID for
each new session.

```csharp
var history = new VespaChatHistoryProvider(snapshots,
    _ => new() { ConversationId = authorizedConversationId });
var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "Assistant",
    ChatHistoryProvider = history
});
```

Messages are serialized as `ChatMessage` objects, preserving roles, supported
content types and tool-call/result IDs. Retrieval returns the saved order without
ranking or search paging. The base history-provider pipeline stamps loaded
messages and skips storage after failed runs. New external input and responses
are stored; injected context-provider messages are excluded to prevent feedback.

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern history "My project is Apollo."
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern history "Continue the project."
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern history --stream "Summarize our decisions."
```

### Caveats

- A conversation is one bounded snapshot. Each write reads and replaces its
  ordered list. This favors small and medium conversations over unbounded audit
  archives. The default payload limit is 4 MiB; reduction, archival and retention
  policies belong to the application.
- Reads use Vespa's document API and do not wait for search indexing. This does
  not provide a transaction across history, a session snapshot and any tools.
- A conflict can be detected after the model and tools ran. Those actions are not
  undone. An interrupted HTTP response also leaves the caller needing to reconcile
  the operation's outcome; this is not an exactly-once execution system.
- Successful streaming runs are saved after enumeration completes. Consume the
  stream fully and save the session afterward. A failed/cancelled run does not add
  its new messages, though a configured before-retrieval reducer may already have
  reduced older history.
- `RawRepresentation`, custom runtime-only objects and external media bytes not
  included in serializable content are not durable storage. Referenced URLs may
  expire. Supply an application media-retention policy for attachments.
- Service-managed chat history and a custom `ChatHistoryProvider` are alternative
  history owners. Do not configure both to replay the same conversation.

## Context and semantic memory

The [custom context example](../samples/Vespa.NET.AgentFramework/ConversationPatterns.cs)
uses `ProvideAIContextAsync` to retrieve a user's preference and expose an update
tool. `StoreAIContextAsync` increments a completed-run counter in
`ProviderSessionState<T>`; the session store makes that state durable. The provider
is shared across sessions, while user and run state live in each session.

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern context "Remember that I prefer C# code examples."
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern context "Which language should you use?"
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern text-search --seed "How does vector search work?"
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern text-search --on-demand "How is a schema deployed?"
```

For an advanced provider, override `InvokingCoreAsync`/`InvokedCoreAsync` when the
default append/filter behavior is insufficient. That transfers responsibility for
merging existing instructions/tools/messages, stamping sources, filtering stored
input and skipping failed runs to the implementation. The sample uses the simple
hooks to retain the framework's base behavior. Microsoft's `CompactionProvider`
demonstrates the advanced pipeline integration in compaction mode.

The separate [`--history-memory` example](../samples/Vespa.NET.AgentFramework/README.md#native-chat-history-memory)
uses Microsoft's `ChatHistoryMemoryProvider` without modification. It saves raw
messages and retrieves related ones across sessions. `--on-demand` exposes its own
recall tool. The VectorData connector supports its dictionary model, `Guid` keys,
string-to-vector generation and indexed scope filters.

### Caveats

- Semantic recall returns a ranked subset, not chronological replay or a
  guarantee that every earlier fact is available. New messages may not be visible
  to search immediately after their document write.
- Native chat-history memory stores messages, not one authoritative current value
  for each preference. Old and revised preferences can both match. The separate
  `--memory`/custom-context examples use a stable key to replace one preference.
- Query and document vectors must use the same model/dimensions with appropriate
  preprocessing. The sample configures Nomic document/query prefixes. Changing
  model or dimensions requires re-embedding stored data and deploying its schema.
- Microsoft's memory provider handles some failures through logging. Configure
  logging and monitor storage/search failures; a successful model response alone
  does not prove a memory was saved. Tests assert the stored data and later recall.
- `EnsureCollectionExistsAsync` checks that the schema is searchable. It does not
  create a schema or verify all fields/ranking settings. See the
  [VectorData mapping contract](../src/Vespa.NET.VectorData/README.md#schema-and-mapping-contract).

## File memory and Harness

`VespaAgentFileStore` implements `AgentFileStore`. Microsoft's `FileMemoryProvider`
supplies write, read, delete, list, grep, replace and line-edit tools. Files and
directories are a bounded JSON tree in one Vespa snapshot. The base store's search
implementation performs regex/glob matching and reports line coordinates using
the same split rule as Microsoft's line editor.

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern file-memory "Save my C# preference in preferences.md."
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern file-memory "Read preferences.md."
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern harness "Save our project plan in plan.md."
```

The sample gives each user/pattern a separate store scope. Harness uses that
store for file memory and `VespaChatHistoryProvider` for history. Built-in todo and
mode state is preserved through session serialization. Skills discovery and hosted
web search are disabled in this local sample.

### Caveats

- Paths are relative, normalized to lowercase and case-insensitive. Absolute
  paths and `.`/`..` traversal are rejected. Empty directories are retained;
  deleting files does not delete directories. No operating-system files are touched.
- This store is for bounded text memory, not a general filesystem or large-blob
  store. Regex search walks files through the document API; it is not a Vespa
  full-text or vector query. A larger collection needs a separate indexed design.
- Single tree mutations retry up to five conditional-write attempts and preserve
  unrelated files. Writing the same file replaces its content. Provider operations
  such as read/replace/write and updating a description/index can involve multiple
  mutations; serialize them per scope when concurrent editing matters.
- A model must actually call a memory tool to save or retrieve a file. File memory
  is not an automatic transcript archive. Microsoft rebuilds its memory index as
  part of tool operations; an interruption can leave an index needing refresh.

## Compaction and retained history

The sample selects Microsoft's strategies through `--compact`:

| Value | Strategy | Effect |
|---|---|---|
| `sliding` | `SlidingWindowCompactionStrategy` | Keeps recent user turns |
| `truncation` | `TruncationCompactionStrategy` | Drops oldest groups to meet a budget |
| `tools` | `ToolResultCompactionStrategy` | Replaces older verbose tool interactions |
| `summary` | `SummarizationCompactionStrategy` | Calls a model to summarize older groups |
| `pipeline` | `PipelineCompactionStrategy` | Applies tool reduction, summary, window and truncation in sequence |

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern compaction --compact pipeline "Continue our discussion."
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern history --reduce-history --compact sliding "Continue with the recent decisions."
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern session --reduce-history --compact sliding "Continue."
```

In compaction mode, `UseAIContextProviders(new CompactionProvider(strategy))` is
registered on `ChatClientBuilder`. The model receives compacted context while
the history provider keeps the full archive. It also runs between model calls
inside the tool loop.

`--reduce-history` instead adapts the strategy with `AsChatReducer()` and configures
the history provider itself. `VespaChatHistoryProviderOptions.ReducerTriggerEvent`
selects before retrieval (the sample default) or after new messages are added.
Reduction is saved back to Vespa at that point. The same approach works with
Microsoft's `InMemoryChatHistoryProvider` in session mode. Existing `IChatReducer`
implementations can be adapted the other way with `ChatReducerCompactionStrategy`.
For ad-hoc use, call `CompactionProvider.CompactAsync(strategy, messages)`.

### Caveats

- Registering `CompactionProvider` directly in agent-level `AIContextProviders`
  runs outside the tool loop; generated summaries can enter persisted history.
  Use builder registration when the archive must remain complete.
- Compaction honors system messages, atomic tool-call/result groups and each
  strategy's minimum-retention floor. Its trigger is not a guarantee of a hard
  token ceiling. Token counts can be estimates; tune budgets for the model.
- Summaries are lossy and add model calls, latency and cost. The sample uses the
  same chat model as the agent; applications can supply a separate summarizer.
- Builder-only compaction does not reduce stored payload size. Persistent history
  reduction removes or summarizes stored content; retain an independent archive
  if that information must remain recoverable.
- Harness applies its compaction strategy within the service-call loop. A custom
  history provider is used as supplied; Harness does not automatically configure
  that provider's persistent reducer.
- Microsoft marks compaction, `AgentFileStore` and hosted session-store contracts
  experimental (`MAAI001`). Examples suppress that diagnostic locally. Public
  APIs and parameter names can differ from current Learn snippets; keep package
  versions pinned and rerun compatibility tests when upgrading.

## Scope, retention and validation

Resolve user, tenant, agent and conversation access from trusted request identity.
Authorize before loading state, including when accepting a continuation ID or a
serialized session. Scope strings, partitions, hashes and Vespa namespaces are
storage identifiers; they do not enforce access control. Never let a model choose
another user's scope. Protect stored conversation content and apply application
retention/deletion policies across snapshots, file trees and semantic records.

```sh
dotnet test tests/Vespa.NET.AgentFramework.Tests -c Release
dotnet test tests/Vespa.NET.VectorData.Tests -c Release
VESPA_AGENT_FRAMEWORK_INTEGRATION_TESTS=1 dotnet test tests/Vespa.NET.AgentFramework.Tests -c Release --filter FullyQualifiedName~LiveVespaTests
VESPA_VECTOR_DATA_INTEGRATION_TESTS=1 dotnet test tests/Vespa.NET.VectorData.Tests -c Release --filter FullyQualifiedName~LiveVespaTests
```

Default tests exercise the published Microsoft classes with deterministic chat and
embedding doubles. Docker tests use dedicated Vespa instances. They verify storage,
ordered replay, stale-write rejection, scope separation, native memory and the
runnable quickstart. They do not measure LLM answer quality, reproduce a specific
host's HTTP stack or test retention in an external model service.

## Prepare packages

Pack the two integrations separately; the base client remains at version 3.1.1:

```sh
dotnet pack src/Vespa.NET.VectorData -c Release -o artifacts/vector-data
dotnet pack src/Vespa.NET.AgentFramework -c Release -o artifacts/agent-framework
python3 scripts/verify-vector-data-package.py artifacts/vector-data
python3 scripts/verify-agent-framework-package.py artifacts/agent-framework
```

Each verifier requires both .NET runtimes and tests the actual package against the
published Vespa.NET client in a fresh consumer project. Sample-only tests run in
the repository suite; package verification exercises the reusable library.

A manual CI run verifies and uploads both artifacts without publishing:

```sh
gh workflow run ci.yml --ref main -f publish_vector_data=false -f publish_agent_framework=false
```

Publication requires a separate manual run from `main`, setting the corresponding
flag to `true` after reviewing the release. Each publish job downloads the same
verified artifact, depends on successful tests and uses the existing NuGet trusted
publishing identity. Its policy must permit the relevant package ID. Normal pushes
and pull requests do not publish these integrations. Connector tags must not start
with `v`, which is reserved for publishing the three base SDK packages.
