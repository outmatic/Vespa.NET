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

`--deploy` installs the `article` and `memory` schemas. Omit it after the first
deployment.

The [memory helper](PreferenceMemory.cs) stores one preference per user under a
stable user/topic key. Updates replace the previous preference, and the write
tool waits until the new text is searchable.
Embedding generation happens before upsert, with Nomic's document prefix; recall
uses its query prefix and filters by user and topic.

`VESPA_MEMORY_USER` sets the user scope (default `demo-user`). Both tools take
the user ID from this configuration. Production applications must resolve the
user from an authenticated request and enforce access controls. The environment
variable and Vespa namespaces do not provide tenant isolation.

## Endpoints and models

| Environment variable | Default |
|---|---|
| `VESPA_ENDPOINT` | `http://localhost:8080` |
| `VESPA_CONFIG_ENDPOINT` | Query endpoint host with port `19071` |
| `OLLAMA_ENDPOINT` | `http://localhost:11434` |
| `OLLAMA_MODEL` | `qwen3:4b` (must support tools) |
| `VESPA_MEMORY_USER` | `demo-user` (memory mode only) |

The embedding model is `nomic-embed-text` with 768 dimensions. The sample adds its
recommended `search_document:` and `search_query:` prefixes. Documentation mode
filters retrieval to published sample documents and asks the agent to cite their
IDs. Memory mode filters preferences by the configured user and topic.

See the [connector README](../../src/Vespa.NET.VectorData/README.md) for schema
requirements, supported operations, lifetime and filtering limitations.
