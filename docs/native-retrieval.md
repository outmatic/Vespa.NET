# Native Vespa retrieval with Microsoft Agent Framework

The Agent Framework sample retrieves supporting passages from Vespa and passes them to Microsoft's `TextSearchProvider` or a function tool. Vespa analyzes the query, finds candidates and ranks the passages before the agent sees them.

The VectorData connector remains useful for portable vector-store access and Microsoft's unmodified semantic-memory provider. Use the native client when you want Vespa fieldsets, query inputs, custom ranking phases and match features. Both use the same caller-owned `VespaClient` and can coexist in one application.

## Run the sample

Follow the [sample setup](../samples/Vespa.NET.AgentFramework/README.md) to start a dedicated Vespa instance and Ollama. Deploy the current application and seed its separate title/body fields:

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- \
  --deploy --seed "How do I deploy an application package?"
```

`--deploy` replaces the application on that development instance. On later runs, choose a retrieval method or omit the flag for semantic search:

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- --retrieval lexical "nearestNeighbor"
dotnet run --project samples/Vespa.NET.AgentFramework -- --retrieval semantic "Find documents with a similar meaning"
dotnet run --project samples/Vespa.NET.AgentFramework -- --retrieval hybrid "Find the nearestNeighbor API reference"
dotnet run --project samples/Vespa.NET.AgentFramework -- --retrieval rrf "How do document updates work?"
dotnet run --project samples/Vespa.NET.AgentFramework -- --pattern text-search --on-demand "How is a schema deployed?"
```

The sample defaults to semantic search with Nomic embeddings. It enables HNSW, targets 100 vector candidates and returns up to three passages.

Use `--retrieval hybrid` for balanced hybrid search. Its lexical branch uses `tokenize` over title and body with optional weakAnd pruning unset. It targets 100 lexical and 100 vector candidates, then reranks up to 100 hits with equal weights after normalization. The [NFCorpus and ArguAna comparison](retrieval-quality.md#check-the-sample-default) measured this balanced hybrid alongside lexical and semantic search.

The [SciFact and FiQA comparison](retrieval-transfer.md) tests the same settings on two more collections. The sample hybrid scored higher than semantic search on SciFact and lower on FiQA. Use these settings as a starting example and evaluate your own queries before choosing a retrieval mode or saved preset.

The [E5 comparison](retrieval-embeddings.md) repeats those configurations with a second embedding pipeline. FiQA's hybrid deficit persists, even though both semantic and hybrid scores improve.

Lexical queries do not invoke the embedding generator; seeding documents still generates embeddings for the vector modes. Chat responses use the configured chat model in every mode.

## Configure native matching and returned context

Pass the following JSON object to the sample with `--retrieval-config path/to/settings.json`. The sample also accepts a tuning file with a `selected` object.

```json
{
  "name": "italian-documentation",
  "mode": "hybrid_normalized",
  "language": "it",
  "textFieldSet": "default",
  "grammar": "tokenize",
  "resultFields": ["title", "body"],
  "summaryClass": "vector_data",
  "vectorCandidates": 100,
  "keywordCandidates": 100,
  "vectorWeight": 0.5,
  "titleWeight": 1,
  "globalPhaseRerankCount": 100,
  "approximate": true,
  "hnswExploreAdditionalHits": 32
}
```

Language, fieldset, grammar, summary and returned fields also have CLI overrides:

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- \
  --retrieval hybrid --retrieval-language it --retrieval-fieldset default \
  --retrieval-grammar tokenize --retrieval-summary vector_data \
  --retrieval-fields title,body "Come funziona una pompa di calore?"
```

Use a corpus and embedding model appropriate to the query language; setting `it` does not translate the English sample documents or change the embedding model. The sample schema accepts a `language` field first in each document and applies `set_language` before indexing title/body. Feed the correct language and re-feed documents if their analysis changes. Query language controls Vespa's query-side analysis, independently of embeddings. A null JSON language leaves detection to Vespa.

The sample uses `tokenize` grammar for natural-language and model-generated queries. Vespa performs weakAnd retrieval, treating quotes and operators as ordinary input punctuation so a quoted paragraph can match individual terms. Use `--retrieval-grammar weakAnd` when you want Vespa's simple query syntax, including quoted phrases and field-qualified terms. `all`, `any`, `phrase` and `raw` are also available. `raw` matches a single native value and is useful with the sample's cased `identifier` attribute for exact codes; it is not a free-text tokenizer.

`resultFields` controls the YQL projection and `summaryClass` selects a deployed document summary. Return `title`, `body` or both for the sample's context formatter; at least one must contain supporting text. A custom `rankProfileOverride` selects an application-owned profile. The example still supplies its existing query inputs; adapt the request directly for profiles with different tensor names or input contracts. Weights, budgets, RRF constants, approximation and exploration are configurable in JSON. Fieldsets, summaries and profiles must exist in the deployed application.

`globalPhaseRerankCount` independently limits global reranking. The sample's built-in hybrid and RRF modes set it to 100. When loading a JSON configuration that omits it, the budget is the sum of the candidate targets. It must be at least the requested result count. Reducing it changes both the shortlist and the population used by cross-hit normalization, so check quality as well as latency.

JSON configurations load their own settings rather than inheriting the sample's built-in defaults. Set `mode` and `grammar` explicitly: omitting `mode` selects `semantic`, and omitting `grammar` selects `weakAnd`. Existing evaluation presets retain their settings.

For `weakAnd` or `tokenize` grammar, optional `weakAndStopwordLimit` and `weakAndAdjustTarget` pass directly to Vespa's native matching controls. The first drops terms above a corpus document-frequency threshold; the second controls a frequency-derived weakAnd score threshold. Both take values in [0,1] and remain unset by default. Dropped terms affect ranking too. These are application choices, not client-side stopword lists. See the official [serving tuning guide](https://docs.vespa.ai/en/performance/feature-tuning.html) and the [quality/latency experiment](retrieval-latency.md).

## What executes in Vespa

The evaluation benchmark links the internal sample helper [`NativeRetrieval.cs`](../samples/Vespa.NET.AgentFramework/NativeRetrieval.cs). [`DocumentationSearch.cs`](../samples/Vespa.NET.AgentFramework/DocumentationSearch.cs) adds Nomic's query prefix only when generating a vector and returns the native hits to Microsoft's provider or a normal function tool.

| Method | Candidate retrieval | Native ranking |
|---|---|---|
| Lexical | `userInput(@lexical_query)` with weakAnd over the title/body fieldset | Weighted BM25 of title and body |
| Semantic | `nearestNeighbor(embedding, query_embedding)` | Vector closeness |
| Hybrid | Union of the lexical and vector candidates | Global-phase normalization with a configurable vector weight |
| RRF | The same union | Weighted native reciprocal ranks, with deployed k=10/30/60/100 profiles |

Raw query text is passed as a bound parameter. Vespa parses and analyzes it; the client does not split it into words. The selected `userInput` grammar controls whether field and phrase syntax is interpreted. Application filters are outside the entire candidate union. In the sample they enforce `published` and the documentation category. For another application, resolve authorized scope from trusted request identity and build its filter there.

Vector and lexical candidate targets are independent. The global reranking budget can be set independently. Targets are search budgets rather than guaranteed recall or result counts. Query features control `vector_weight` and `title_weight`; the deployed profile owns the calculation. BM25 and vector scores are not assumed to share a scale.

With `approximate: true`, Vespa can use its HNSW vector index to avoid comparing the query with every stored vector. `hnswExploreAdditionalHits` increases the search effort in that index. More exploration can improve agreement with exact search, at the cost of latency; a very selective filter can make Vespa choose exact search instead.

Results expose native match features (`lexical_score`, `semantic_score` and vector distance) for diagnostics. An HTTP success with backend errors, incomplete coverage or missing supporting text fails retrieval instead of presenting an empty or partial context as a successful search.

To use a different input tensor or summary in your application, build a `VespaSearchRequest` against your deployed schema.

## Use a saved configuration

Pass a settings file to reuse a configuration across runs. The checked-in NFCorpus profile, for example, contains the settings selected on its development queries.

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- \
  --pattern text-search \
  --retrieval-config benchmarks/Vespa.NET.RetrievalEvaluation/profiles/nfcorpus-nomic.json \
  "Find the deployment documentation"
```

To select weights and candidate budgets for your own corpus, follow the [quality evaluation guide](retrieval-quality.md). For examples of the sample's lexical behavior, including exact codes and Italian phrases, see the [live matching tests](vespa-native-validation.md#native-configuration-and-lexical-regression-checks).
