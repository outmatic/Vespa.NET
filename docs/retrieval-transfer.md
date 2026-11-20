# How hybrid retrieval performs on SciFact and FiQA

The balanced hybrid configuration scored higher than semantic search on SciFact, where the task is to find papers relevant to a scientific claim (0.7214 vs. 0.7028 nDCG@10). On FiQA, which retrieves answers to financial questions, the same configuration scored about 15% lower (0.3159 vs. 0.3706). The sample defaults to semantic search. Select `--retrieval hybrid` to use the balanced settings measured here.

These results use Nomic embeddings. The [E5 comparison](retrieval-embeddings.md) repeats the same configurations on both collections with `intfloat/e5-base-v2`. E5 improves both FiQA scores, but balanced hybrid still trails semantic search.

## Results

The evaluation requested ten hits per query. nDCG@10 measures how highly the judged relevant documents rank. Precision@3 shows the fraction of the first three hits judged relevant, the cutoff closest to the sample's maximum of three passages. Recall@10 is the fraction of known relevant documents returned. These scores measure the passages retrieved before the agent generates an answer.

| Corpus | Settings | nDCG@10 | Precision@3 | Recall@10 | Local serial p95 (ms) |
|---|---|---:|---:|---:|---:|
| SciFact | Balanced hybrid | 0.7214 | 0.2589 | 0.8504 | 16.86 |
| SciFact | Lexical | 0.6695 | 0.2444 | 0.8038 | 15.54 |
| SciFact | Semantic | 0.7028 | 0.2556 | 0.8436 | 15.40 |
| SciFact | ArguAna quality | 0.7230 | 0.2600 | 0.8612 | 19.11 |
| SciFact | ArguAna latency | 0.7160 | 0.2600 | 0.8404 | 16.91 |
| FiQA | Balanced hybrid | 0.3159 | 0.1878 | 0.3811 | 5.21 |
| FiQA | Lexical | 0.2529 | 0.1409 | 0.3122 | 3.89 |
| FiQA | Semantic | 0.3706 | 0.2243 | 0.4422 | 4.07 |
| FiQA | ArguAna quality | 0.3729 | 0.2217 | 0.4407 | 10.25 |
| FiQA | ArguAna latency | 0.3445 | 0.2104 | 0.3995 | 5.59 |

The ArguAna quality preset had the highest observed nDCG@10 on both collections. On FiQA its score was close to semantic search, while its Precision@3 was lower (0.2217 vs. 0.2243) and its p95 was higher (10.25 vs. 4.07 ms). The ArguAna latency preset reached 0.3445 nDCG@10 on FiQA. These presets change the vector weight, candidate budgets, reranking limit and pruning together, so the comparison cannot isolate the contribution of any one setting.

The table below pairs each hybrid configuration with semantic search on the same queries. Each query contributes one nDCG difference after averaging its three repeats. The 95% percentile intervals use 10,000 bootstrap resamples of those query differences (seed 1729), without adjustment for multiple comparisons.

| Corpus | Settings | Mean nDCG@10 difference | 95% interval |
|---|---|---:|---|
| SciFact | Balanced hybrid | +0.0186 | [-0.0083, +0.0457] |
| SciFact | ArguAna quality | +0.0201 | [+0.0013, +0.0391] |
| SciFact | ArguAna latency | +0.0132 | [-0.0084, +0.0342] |
| FiQA | Balanced hybrid | -0.0547 | [-0.0745, -0.0349] |
| FiQA | ArguAna quality | +0.0024 | [-0.0123, +0.0167] |
| FiQA | ArguAna latency | -0.0261 | [-0.0444, -0.0078] |

For the sample hybrid, the SciFact interval includes both gains and losses relative to semantic search. On FiQA the entire interval falls below zero. The FiQA quality preset's interval also includes zero. Scores give zero credit to unjudged documents. The intervals use `bootstrap_interval` from the [tuning helper](../scripts/tune-retrieval.py).

## How the comparison ran

| Collection | Documents | Test queries | Published query–document judgments |
|---|---:|---:|---:|
| SciFact | 5,183 | 300 | 339 |
| FiQA | 57,638 | 648 | 1,706 |

All five [configurations](../benchmarks/Vespa.NET.RetrievalEvaluation/profiles/cross-corpus-transfer.json) were fixed before testing either collection. Lexical search uses BM25 over title and body. Semantic search uses document embeddings with HNSW enabled. Each hybrid configuration combines both candidate sets and normalizes the scores before mixing them. The three hybrid modes use title weight 1 and `tokenize` grammar.

| Hybrid settings | Vector target | Lexical target | Global rerank limit | Vector weight | weakAnd pruning |
|---|---:|---:|---:|---:|---|
| Balanced hybrid | 100 | 100 | 100 | 0.50 | Unset |
| ArguAna quality preset | 600 | 300 | 900 | 0.75 | Enabled |
| ArguAna latency preset | 200 | 100 | 100 | 0.75 | Enabled |

Both ArguAna presets use `weakAndStopwordLimit = 0.6` and `weakAndAdjustTarget = 0.01`. These are the same settings selected on ArguAna. The SciFact and FiQA judgments are used only to score the fixed configurations.

On 20 November 2026, all 14,220 timed requests completed successfully. Each of the five configurations ran three times on every query, interleaved on a fresh dedicated instance per corpus. All documents, queries and judgments were retained. Timings exclude ten warmups per configuration, separate diagnostic traces and embedding generation. Ollama was stopped before timing. These serial measurements do not establish throughput under concurrent load.

## Prepare the collections

The versioned manifests in `benchmarks/Vespa.NET.RetrievalEvaluation/datasets/` identify the official BEIR Hugging Face revisions. The UKP archive host was unavailable. The fetch command checks the compressed and uncompressed file hashes and writes the deterministic BEIR-layout ZIP only after all files pass.

In both collections, query IDs and document IDs belong to separate namespaces. An equal numeric ID can identify a financial question and an unrelated answer. The evaluation keeps these documents eligible, unlike BEIR's default post-retrieval removal of equal IDs. SciFact has no overlapping IDs. FiQA has 55, and none of those pairs has a positive judgment. Retaining them allows unjudged documents to compete for a place in the ranking.

Before indexing or embedding, preparation replaces each character Vespa rejects with one space (U+0020). This changes six characters across five FiQA document bodies and none in SciFact. All search modes use the same cleaned text. `metadata.textPreprocessing` records the rule and the affected fields, document IDs and code points. Source hashes and judgments are unchanged. See [Vespa's text character rules](https://github.com/vespa-engine/vespa/blob/master/vespajlib/src/main/java/com/yahoo/text/Text.java).

FiQA has 38 empty documents. One is among the six documents judged relevant to query 5206. The prepared dataset lists them in `emptyDocumentIds` and retains every judgment. Nomic embeds the document prefix alone for those records, without adding filler text.

From the repository root, use a fresh output directory. Fetch both collections and prepare their embeddings with the same local Ollama `nomic-embed-text` model (digest `0a109f422b47e3a30ba2b10eca18548e944e8a23073ee3f3e947efcf3c45e59f`). Preparation uses Nomic's `search_document: ` and `search_query: ` prefixes. It requests an 8,192-token context and permits truncation at the model's actual 2,048-token limit. BM25 indexes the full text.

```sh
base=artifacts/retrieval-transfer-reproduction
for corpus in scifact fiqa; do
  mkdir -p "$base/$corpus"
  manifest="benchmarks/Vespa.NET.RetrievalEvaluation/datasets/$corpus-source.json"
  uv run scripts/retrieval-quality.py fetch \
    --source-manifest "$manifest" --out "$base/$corpus.zip"
  uv run scripts/retrieval-quality.py prepare \
    --corpus "$corpus" --split test --archive "$base/$corpus.zip" \
    --source-url "https://huggingface.co/datasets/BeIR/$corpus" \
    --source-manifest "$manifest" \
    --no-ignore-identical-ids --context-length 8192 --truncate \
    --out "$base/$corpus/prepared.json"
done
```

## Reproduce the searches

Use a fresh dedicated Vespa instance for each corpus. The deployment command replaces its active application and feeds the full corpus. The example assumes query and configuration ports 18086 and 19076. Repeat with `corpus=fiqa` against the second fresh instance, adjusting the ports if needed. Use the measured image `vespaengine/vespa@sha256:93268edd995b4e671be33babf7ebac55155fd0f330bb0b02123d1fbf3a9d1df0`. See the [dedicated instance setup](retrieval-quality.md#run-the-connector-baseline).

```sh
base=artifacts/retrieval-transfer-reproduction
corpus=scifact
dotnet build benchmarks/Vespa.NET.RetrievalEvaluation -c Release
dotnet benchmarks/Vespa.NET.RetrievalEvaluation/bin/Release/net10.0/Vespa.NET.RetrievalEvaluation.dll \
  --native --latency --dataset "$base/$corpus/prepared.json" \
  --config benchmarks/Vespa.NET.RetrievalEvaluation/profiles/cross-corpus-transfer.json \
  --endpoint http://localhost:18086 --deploy --config-endpoint http://localhost:19076 --feed \
  --k 10 --repeats 3 --warmup 10 --timeout-minutes 30 \
  --out "$base/$corpus/run" --server-label 'record image digest, hardware and Docker limits'

uv run scripts/retrieval-latency.py --dataset "$base/$corpus/prepared.json" \
  --run "$base/$corpus/run/latency-results.json" --reference semantic
```

The completed runs recorded the following SHA-256 hashes.

| Input | SHA-256 |
|---|---|
| SciFact | `e091389e902ddd06198fe28baeb8d1b240131eec7d2b6e76d9f609bdbc2e9c9b` |
| FiQA | `91bc1c86506caab07ffecebbd37d68983c146a7eb5414df966395f2741cf3614` |
| Configuration | `f904e2bdefc0be62f80c76748cad8ced6c9d2ac507ddc4bb1ca223a89903d7b1` |
| Schema | `9b1347dffa0ca25ef35458ca7c0bb2a9c40c7e42a26b61626e511808fe217eb1` |
