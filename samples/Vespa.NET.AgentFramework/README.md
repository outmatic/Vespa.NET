# Vespa + Microsoft Agent Framework

A .NET 10 console sample covering Microsoft Agent Framework conversation and
memory patterns with Vespa: retrieval, preferences, sessions, chronological and
semantic history, file memory, compaction and Harness.
All modes use Ollama for embeddings and chat. Run these commands from the repository root.

See the [conversation patterns guide](../../docs/agent-framework.md) for the full
.NET pattern map, configuration and caveats.

For an existing cluster, [reuse the deployed application and configure the client](../../docs/agent-framework.md#reuse-an-existing-client-and-deployment).
The guide also covers custom `services.xml`, generated application options, Cloud
data-plane access and deployment through the Cloud CLI/control plane. The sample's
`--deploy` path targets a self-hosted config server.

## First run

Install .NET 10, Docker and Ollama, start Ollama, then start a dedicated Vespa instance:

```sh
docker run --detach --name vespa-vector-data --hostname vespa-container \
  --publish 127.0.0.1:8080:8080 --publish 127.0.0.1:19071:19071 \
  vespaengine/vespa:8
ollama pull nomic-embed-text
ollama pull qwen3:4b
dotnet run --project samples/Vespa.NET.AgentFramework -- --deploy --seed "How does vector search work?"
```

`--deploy` waits for the config server, deploys the included `app/` package through
`VespaClient.Admin`, and waits for application readiness. **It replaces the active
Vespa application on the target instance. Use a dedicated development instance.**
`--seed` generates embeddings for three sample documents, writes them, and waits
until they are searchable. For subsequent questions:

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- "How does hybrid search work?"
```

To deploy without running embeddings or the agent (Ollama is not required):

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- --deploy-only
```

The whole run has a five-minute timeout. Deployment files are copied to build and
publish output; a published executable resolves `app/` beside itself, independently
of the working directory.

## Remember a preference across runs

`--memory` registers `remember_preference` and `recall_preferences`, backed by the
`memory` schema. The tools save and retrieve the configured user's preferred
programming language for code examples.

With the same Docker instance and Ollama models running, execute these as two
separate commands:

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- --deploy --memory "Remember that I prefer code examples in C#."
dotnet run --project samples/Vespa.NET.AgentFramework -- --memory "Which programming language should you use for my code examples?"
```

`--deploy` installs `article`, `memory`, `chathistory` and `agentstate`. Omit it after the first
deployment.

The [memory helper](PreferenceMemory.cs) stores one preference per user under a
stable user/topic key. Updates replace the previous preference. Recall reads that
exact key and verifies its user/topic metadata; it needs no query embedding or
search-visibility polling. Embedding generation still happens before upsert, with
Nomic's document prefix, so the stored record remains usable for semantic search.

`VESPA_MEMORY_USER` sets the user scope (default `demo-user`). Both tools take
the user ID from this configuration. Production applications must resolve the
user from an authenticated request and enforce access controls. The environment
variable and Vespa namespaces do not provide tenant isolation.

## Native chat-history memory

`--history-memory` attaches Microsoft's `ChatHistoryMemoryProvider` through
`ChatClientAgentOptions.AIContextProviders`. It saves conversation messages after
each response and retrieves relevant messages before the next model invocation.
It uses the provider's own dictionary model and scope filters without a custom
save or recall tool. These records contain message text and metadata, not complete
serialized messages with all tool or multimodal content. Warning/error diagnostics
are printed without conversation text or exception messages; a model response can
succeed even if the provider logs a memory failure.

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- --deploy --history-memory "I prefer code examples in C#."
dotnet run --project samples/Vespa.NET.AgentFramework -- --history-memory "Which programming language should you use for my code examples?"
```

Each process creates a new session. Messages are stored with application, agent,
user and session IDs. Searches use application, agent and user, so later sessions
can recall earlier messages for the same user. Set `VESPA_MEMORY_USER` to change
the demo user. Search visibility can lag behind document writes.

To let the model decide when to search, enable the provider's own retrieval function:

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- --history-memory --on-demand "What language do I prefer?"
```

The [`chathistory` schema](app/schemas/chathistory.sd) matches the provider's field
names and 768-dimensional vector. The connector generates embeddings from its
string vector inputs, using the configured document prefix; text searches use the
query prefix. `EnsureCollectionExistsAsync` verifies that the deployed schema is
searchable and does not deploy it.

This mode retrieves related conversation messages. The `--memory` mode above
maintains one current preference through explicit tools. For exact conversation
replay, manage the agent's session history separately.

## Sessions, history, context, files and compaction

`--pattern` selects a conversation pattern. Each mode persists its session in
Vespa so separate processes can resume it. The default conversation is
`demo-conversation`; set `VESPA_CONVERSATION_ID` and `VESPA_MEMORY_USER` to select
the demo conversation and user. Modes have separate storage scopes.

| Pattern | Behavior |
|---|---|
| `session` | Microsoft's in-memory history, saved in a complete session snapshot |
| `history` | Ordered messages in `VespaChatHistoryProvider` |
| `context` | Custom `AIContextProvider` retrieves a preference and exposes its update tool |
| `text-search` | Microsoft's `TextSearchProvider` with Vespa retrieval; `--on-demand` enables its tool |
| `file-memory` | Microsoft's `FileMemoryProvider` over a Vespa file store |
| `compaction` | Compacts model requests inside the tool loop while retaining the stored history |
| `harness` | Microsoft's Harness with Vespa history, file memory and compaction |

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern session "My project is Apollo."
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern session "What is my project's name?"
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern history --stream "Remember the deployment decisions."
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern context "Remember that I prefer C# examples."
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern text-search --seed "How does vector search work?"
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern file-memory "Write my C# preference to preferences.md."
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern file-memory "Read preferences.md."
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern compaction --compact pipeline "Continue the discussion."
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern harness "Save the project plan to plan.md."
```

`--compact` selects `sliding` (default), `truncation`, `tools`, `summary` or
`pipeline`. `--reduce-history` configures the history provider's reducer and
persists the reduced history; request-only compaction leaves the archive complete.
`--stream` fully consumes a response stream before saving its session.

Snapshots have a 4 MiB default limit and reject stale concurrent writes. File
memory is a bounded text tree with lowercase paths. The sample does not provision
a remote model service or an HTTP host. For those cases, the session store preserves
service conversation IDs and implements Microsoft's hosted `AgentSessionStore`
contract. See the guide for concurrency, retention and experimental-API caveats.

## Endpoints and models

| Environment variable | Default |
|---|---|
| `VESPA_ENDPOINT` | `http://localhost:8080` |
| `VESPA_CONFIG_ENDPOINT` | Query endpoint host with port `19071` |
| `OLLAMA_ENDPOINT` | `http://localhost:11434` |
| `OLLAMA_MODEL` | `qwen3:4b` (must support tools) |
| `VESPA_MEMORY_USER` | `demo-user` (memory and `--pattern` modes) |
| `VESPA_CONVERSATION_ID` | `demo-conversation` (`--pattern` modes) |

The embedding model is `nomic-embed-text` with 768 dimensions. The sample adds its
recommended `search_document:` and `search_query:` prefixes. Documentation mode
filters retrieval to published sample documents and asks the agent to cite their
IDs. Memory mode filters preferences by the configured user and topic.

See the [connector README](../../src/Vespa.NET.VectorData/README.md) for schema
requirements, supported operations, lifetime and filtering limitations.
