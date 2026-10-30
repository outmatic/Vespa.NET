# Vespa + Microsoft Agent Framework

A .NET 10 console sample using Vespa vector search as an agent retrieval tool,
with Ollama for embeddings and chat. Run these commands from the repository root.

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

## Endpoints and models

| Environment variable | Default |
|---|---|
| `VESPA_ENDPOINT` | `http://localhost:8080` |
| `VESPA_CONFIG_ENDPOINT` | Query endpoint host with port `19071` |
| `OLLAMA_ENDPOINT` | `http://localhost:11434` |
| `OLLAMA_MODEL` | `qwen3:4b` (must support tools) |

The embedding model is `nomic-embed-text` with 768 dimensions. The sample adds its
recommended `search_document:` and `search_query:` prefixes, filters retrieval to
published sample documents, and asks the agent to cite their IDs.

See the [connector README](../../src/Vespa.NET.VectorData/README.md) for schema
requirements, supported operations, lifetime and filtering limitations.
