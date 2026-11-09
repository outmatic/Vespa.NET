# Vespa + Microsoft Agent Framework

A .NET 10 console sample showing two uses of Vespa with Microsoft Agent Framework:
documentation retrieval and a user preference remembered across separate runs.
Both use Ollama for embeddings and chat. Run these commands from the repository root.

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
The Vespa CLI is not needed for this sample.

`--seed` generates embeddings for three sample documents, writes them, and waits
until they are searchable. Both operations are explicit. For subsequent questions:

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

The default mode retrieves preloaded documentation. `--memory` instead gives the
agent `remember_preference` and `recall_preferences` tools backed by a separate
`memory` schema. The first tool saves the user's explicitly stated preference;
the second retrieves it for the configured user. No documentation seed is used.

With the same Docker instance and Ollama models running, execute these as two
separate commands:

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- --deploy --memory "Remember that I prefer code examples in C#."
dotnet run --project samples/Vespa.NET.AgentFramework -- --memory "Which programming language should you use for my code examples?"
```

The second process creates a new agent and receives no messages from the first
conversation. It must call `recall_preferences` to obtain the saved preference
from Vespa. `--deploy` includes both schemas; omit it after deploying the current
application package. As above, use a dedicated instance because deployment
replaces the active application.

The [memory helper](PreferenceMemory.cs) implements an intentionally narrow policy:
one preferred code-example language per user. A stable user/topic key replaces a
previous preference, and the write tool waits until the new text is searchable.
Embedding generation happens before upsert, with Nomic's document prefix; recall
uses its query prefix and filters by user and topic.

`VESPA_MEMORY_USER` selects a demo user scope (default `demo-user`). The model
cannot supply a user ID to either tool. This variable is not authentication;
production applications must derive identity from an authenticated request and
enforce access controls. Namespaces are not a tenant isolation boundary.

These memory tools are sample application code over VectorData. They do not
register a built-in Agent Framework memory/context provider or persist the full
conversation transcript. Model tool selection remains dependent on the chat
model; the repository tests exercise the real Agent Framework tool loop using
deterministic model and embedding doubles.

## Endpoints and models

| Environment variable | Default |
|---|---|
| `VESPA_ENDPOINT` | `http://localhost:8080` |
| `VESPA_CONFIG_ENDPOINT` | Query endpoint host with port `19071` |
| `OLLAMA_ENDPOINT` | `http://localhost:11434` |
| `OLLAMA_MODEL` | `qwen3:4b` (must support tools) |
| `VESPA_MEMORY_USER` | `demo-user` (memory mode only; demo scope, not authentication) |

The embedding model is `nomic-embed-text` with 768 dimensions. The sample adds its
recommended `search_document:` and `search_query:` prefixes. Documentation mode
filters retrieval to published sample documents and asks the agent to cite their
IDs. Memory mode filters preferences by the configured user and topic.

See the [connector README](../../src/Vespa.NET.VectorData/README.md) for schema
requirements, supported operations, lifetime and filtering limitations.
