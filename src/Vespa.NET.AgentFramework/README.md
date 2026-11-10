# Vespa.NET.AgentFramework

Vespa persistence for Microsoft Agent Framework: complete sessions, ordered chat
history and file memory. Targets .NET 8 and .NET 10; uses Microsoft.Agents.AI 1.26.0
and Vespa.NET 3.1.1 or later.

See the [conversation patterns guide](https://github.com/outmatic/Vespa.NET/blob/main/docs/agent-framework.md) and runnable
[sample](https://github.com/outmatic/Vespa.NET/tree/main/samples/Vespa.NET.AgentFramework) for configuration and caveats.

Build or pack this optional project from the repository root:

```sh
dotnet build src/Vespa.NET.AgentFramework -c Release
dotnet pack src/Vespa.NET.AgentFramework -c Release -o artifacts/agent-framework
```

Deploy the sample's `agentstate` schema before using these providers. They use
Vespa's document API directly; embeddings and a search index are not required.
`VespaAgentStateStore` keeps versioned JSON snapshots, with conditional writes to
reject concurrent overwrites. Each snapshot is limited to 4 MiB by default.

```csharp
using Microsoft.Agents.AI;
using Vespa.AgentFramework;

var stateStore = new VespaAgentStateStore(client, new()
{
    Namespace = "knowledge",
    Scope = "my-app/my-agent/authenticated-user"
});
var history = new VespaChatHistoryProvider(stateStore);
var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "Assistant",
    ChatHistoryProvider = history
});
```

Resolve scope and conversation access from trusted application identity. Storage
scopes and hashed document IDs do not authorize access. The caller owns the client.

Snapshots are bounded, and each write replaces one JSON document. Concurrent stale
writes fail instead of overwriting another branch. Run each conversation serially;
an agent invocation and its tool side effects are not part of a storage transaction.
See the guide for revision tracking, retention, file editing and compaction caveats.

For semantic recall with Microsoft's `ChatHistoryMemoryProvider`, use the separate
`Vespa.NET.VectorData` package. Session snapshots, chronological history and semantic
memory serve different purposes and can be combined.
