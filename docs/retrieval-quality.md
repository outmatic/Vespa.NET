# Measure lexical, semantic and hybrid retrieval quality

Use published relevance judgments to compare which documents BM25, dense vector search and two hybrid ranking methods return. The examples use NFCorpus, a biomedical collection with 3,633 documents. The primary score, nDCG@10, measures how well the first ten results place relevant documents near the top. Higher is better. For an agent that receives only three passages, Precision@3 measures the fraction of those passages with a positive judgment. Evaluating the agent's answer requires a separate test.

The connector baseline indexes title and body together and uses VectorData's search methods. The [native sample](native-retrieval.md) indexes the fields separately and lets Vespa parse the query. Both experiments use precomputed embeddings and exact vector search. For approximate search with filters, see the [native validation results](vespa-native-validation.md).

## Prepare real embeddings and judgments

Requirements: .NET 10, Python 3.11–3.13 and `uv`, Docker, and an Ollama embedding model. Downloads, model installation, server startup, deployment and feeding are all explicit. Example from the repository root:

```sh
mkdir -p artifacts/retrieval-quality
curl -fsSL https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/nfcorpus.zip \
  -o artifacts/retrieval-quality/nfcorpus.zip
ollama pull nomic-embed-text

uv run scripts/retrieval-quality.py prepare \
  --archive artifacts/retrieval-quality/nfcorpus.zip \
  --split test --truncate --out artifacts/retrieval-quality/nfcorpus-test.json
```

Preparation keeps all 3,633 documents and all 323 judged test queries. For a quick functional check, `--query-limit 10` selects the first ten query IDs in stable lexicographic order, retaining the **entire** document corpus. It is a smoke test, not evidence for selecting a default. `--split dev` prepares the development set for tuning; reserve the test split for the final comparison. No ranking weights are tuned automatically on test judgments.

`--ollama`, `--model`, `--batch-size`, `--document-prefix` and `--query-prefix` and `--context-length` are configurable. The defaults use Nomic's `search_document: ` and `search_query: ` prefixes. If you change the model, supply the preprocessing required by that model as well. Without `--truncate`, overlength input fails preparation. The example explicitly allows Ollama to truncate embedding inputs to the model's supported context; this policy is recorded in the dataset and report. The model's actual limit can be lower than the requested context length. BM25 still indexes the complete text, while semantic retrieval may lose its tail. Preparation replaces code points rejected by Vespa with one space each, before both indexing and embedding, and records the affected fields in `metadata.textPreprocessing`. Raw source hashes and judgments remain unchanged. Embeddings are cached by model digest, prefix, context/truncation settings and exact input text in `embedding-cache.jsonl` beside the output. A failed call can be rerun without re-embedding completed inputs. Existing dataset output is not overwritten.

The dataset records its source archive SHA-256, model digest and prefixes. The runner fingerprints the complete prepared file, including judgments and vectors. Keep this file to reproduce the exact same comparison; regenerated embeddings may differ between model versions or hardware. Downloaded source data, model weights, caches and reports stay under the ignored `artifacts/` directory.

For SciFact and FiQA, the [transfer guide](retrieval-transfer.md) uses checksum-pinned source manifests and the explicit `fetch` command. It also explains how empty source documents and separate query/document ID namespaces are handled. The preparer supports all four corpora with `--corpus`.

The optional [E5 preparation and comparison](retrieval-embeddings.md) uses a pinned local PyTorch model with E5's prefixes, pooling and 512-token limit. It feeds precomputed vectors to the same evaluator and documents where embedding inputs are truncated. The sample continues to use its configured Ollama model.

## Run the connector baseline

`--deploy` replaces the **whole application** on the specified config endpoint. Use a separate disposable Vespa container. Choose unused localhost ports and a unique name, and pin the image digest for comparisons:

```sh
docker run --detach --name vespa-retrieval-quality --hostname vespa-container \
  --publish 127.0.0.1:18086:8080 --publish 127.0.0.1:19076:19071 \
  --env VESPA_IGNORE_NOT_ENOUGH_MEMORY=true vespaengine/vespa:8

dotnet run -c Release --project benchmarks/Vespa.NET.RetrievalEvaluation -- \
  --dataset artifacts/retrieval-quality/nfcorpus-test.json \
  --endpoint http://localhost:18086 \
  --deploy --config-endpoint http://localhost:19076 --feed \
  --k 10 --candidates 100 --out artifacts/retrieval-quality/test-run \
  --server-label 'record image digest, CPU/RAM and Docker limits'

uv run scripts/retrieval-quality.py report \
  --dataset artifacts/retrieval-quality/nfcorpus-test.json \
  --run artifacts/retrieval-quality/test-run/results.json
```

On the same prepared instance, omit `--deploy`, `--config-endpoint` and `--feed` for read-only repetitions. A fresh output directory is required. Use a fresh container for another corpus/model/dimension or candidate budget: RRF reranking budgets are declared in the deployed schema. The recorded `schema.sd` is the expected application, not read-back validation of a reused deployment. IDs and the dataset fingerprint detect incomplete/wrong corpora, not ranking-profile configuration changes. Do not mix multiple datasets in this dedicated instance.

The runner waits for complete corpus visibility and rejects query errors or incomplete search coverage. All four modes use the same text (title plus body), query keywords, corpus and result cutoff. The lexical branch uses the same `weakAnd` construction as the connector's hybrid branch. Candidate targets are the same for dense and lexical retrieval in both hybrid profiles. Their global reranking budget is twice that target; these values are starting configurations, not measured optima. Normalization and RRF run in Vespa's [native global phase](https://docs.vespa.ai/en/ranking/phased-ranking.html).

Vectors are searched exactly; HNSW is deliberately excluded. Use the separate [ANN benchmark](vector-search-benchmarks.md) to measure the additional recall and latency tradeoff after choosing retrieval/ranking. Embeddings are precomputed, so recorded search times exclude embedding generation. Requests run serially with shuffled mode order (seed 1729), including cold requests; timings are diagnostic rather than throughput or reliable tail-latency measurements.

## Read the quality report

The runner writes `results.json` only after every query/mode completes, including explicit empty rankings. The offline report rejects incomplete runs, duplicated queries/hits, unknown document IDs and mismatched dataset hashes. Backend rank order is preserved even when scores tie; different modes' raw score scales are not compared. Empty results count as zero in the aggregate denominator.

Reports contain `QUALITY.md` and `quality.json`, with aggregate and per-query scores. `ir_measures==0.4.3` computes nDCG using graded relevance. Recall and reciprocal rank treat judgments >= 1 as relevant. Unjudged documents count as nonrelevant, so incomplete judgments can penalize useful unseen documents. The NFCorpus collection has available positive judgments for every query and does not measure abstention. The preparation script also supports `--corpus arguana`. That archive contains five judged document IDs absent from its corpus: they are recorded explicitly and their five zero-score queries remain in all metric denominators. Identical query/document IDs are excluded during native retrieval, following BEIR's evaluation convention. The legacy connector runner rejects datasets needing this exclusion. Preserve these distinctions for other datasets.

Offline checks for denominator, ordering and input-validation errors:

```sh
uv run scripts/retrieval-quality.py self-test
```

## NFCorpus connector results

On 2026-11-19, the complete NFCorpus test split (3,633 documents, 323 queries) was evaluated with `nomic-embed-text` (768 dimensions, digest `0a109f422b47e3a30ba2b10eca18548e944e8a23073ee3f3e947efcf3c45e59f`), the Nomic prefixes, explicit embedding-input truncation, exact angular search, 100 candidates per hybrid branch, 200 global reranking hits and k=10. Vespa image: `vespaengine/vespa@sha256:93268edd995b4e671be33babf7ebac55155fd0f330bb0b02123d1fbf3a9d1df0`. Prepared dataset SHA-256: `589a0d4c0bd250e95c8f99df8b37488233ef84138078bfabcbbbf8da36fe5099`.

| Mode | nDCG@10 | Recall@10 | MRR@10 |
|---|---:|---:|---:|
| BM25 | 0.3196 | 0.1524 | 0.5265 |
| Dense semantic | 0.3475 | 0.1722 | 0.5335 |
| Hybrid normalized | 0.3603 | 0.1767 | 0.5670 |
| Hybrid RRF | 0.3457 | 0.1690 | 0.5507 |

Normalized hybrid improved mean nDCG by 3.69% relative to dense retrieval, but won on 103 queries, lost on 83 and tied on 137. RRF's mean was slightly lower than dense retrieval. No weights or candidate budgets were tuned on this test split.

## Tune the native sample

Use a dedicated instance for native tuning. The tuner deploys the multi-field schema and feeds the corpus. Prepare both splits using the same model, preprocessing and embedding cache:

```sh
uv run scripts/retrieval-quality.py prepare \
  --archive artifacts/retrieval-quality/nfcorpus.zip --split dev --truncate \
  --out artifacts/retrieval-quality/nfcorpus-native-dev.json
uv run scripts/retrieval-quality.py prepare \
  --archive artifacts/retrieval-quality/nfcorpus.zip --split test --truncate \
  --out artifacts/retrieval-quality/nfcorpus-native-test.json

dotnet build benchmarks/Vespa.NET.RetrievalEvaluation -c Release
uv run scripts/tune-retrieval.py \
  --dev artifacts/retrieval-quality/nfcorpus-native-dev.json \
  --test artifacts/retrieval-quality/nfcorpus-native-test.json \
  --endpoint http://localhost:18086 --deploy --config-endpoint http://localhost:19076 \
  --out artifacts/retrieval-quality/native-tuning \
  --server-label 'record the actual image digest and hardware'
```

The tuner checks that development and test query IDs are disjoint, the document embeddings match and the deployed schema hash stays the same. It writes `selection.json` before opening the test dataset. If configurations tie on development nDCG@10, it favors the smaller candidate budget.

## NFCorpus native results

The subsequent native experiment searched 75 configurations and 75 refinements on all 324 development queries, then froze one configuration per family before testing on the 323 disjoint test query IDs. It used the same document/query embeddings as the baseline, with separate title/body indexes and Vespa's native `userInput` parsing. Selection used mean development nDCG@10, without test-based parameter selection. The test queries had already been evaluated in the connector baseline.

| Method | nDCG@10 | Precision@10 | MRR@10 | Precision@3 | Precision@1 |
|---|---:|---:|---:|---:|---:|
| Native lexical | 0.3004 | 0.2105 | 0.5147 | 0.3406 | 0.4334 |
| Semantic | 0.3475 | 0.2582 | 0.5335 | 0.3839 | 0.4427 |
| Native normalized hybrid | 0.3545 | 0.2567 | 0.5844 | 0.4014 | 0.5046 |
| Native RRF | 0.3416 | 0.2511 | 0.5555 | 0.3829 | 0.4737 |

The normalized configuration uses 600 vector candidates, 300 lexical candidates, vector weight 0.75 and title weight 1. It improves first-result relevance and MRR in this test, but Precision@10 and Recall@10 do not improve. Its mean nDCG delta versus semantic is +0.0070; a paired percentile bootstrap with 10,000 resamples (seed 1729) gives a 95% interval of [-0.0074, +0.0207]. The result does not establish a reliable nDCG improvement. Top-three/top-one metrics are secondary measurements of the same frozen rankings, not additional selection objectives.

The single-field connector baseline scored 0.3603 nDCG@10. It used a different field layout and lexical parser.

The [NFCorpus configuration](../benchmarks/Vespa.NET.RetrievalEvaluation/profiles/nfcorpus-nomic.json) records the model, dataset and schema hashes, and selected inputs.

The console application without arguments or with `--help` only prints usage; it never starts a service or changes data. Heavy evaluation stays outside the normal `dotnet test` path. Stop/remove only the disposable container you created when finished:

```sh
docker stop vespa-retrieval-quality
docker rm vespa-retrieval-quality
```

## Check the sample default

The [saved comparison settings](../benchmarks/Vespa.NET.RetrievalEvaluation/profiles/sample-default.json) capture an earlier sample configuration, exported before measurement. Its normalized hybrid uses equal vector and lexical weights, 100 candidates per method, and global reranking up to 100. It enables HNSW and uses `tokenize` without optional weakAnd pruning. The semantic configuration in this snapshot matches the current sample default. The lexical configuration provides a text-only comparison.

On 2026-11-20, each corpus ran on a fresh dedicated instance using the pinned Vespa image above and the existing Nomic embeddings. All 323 NFCorpus and 1,406 ArguAna test queries ran once per mode: 5,187 measured searches in total. Warmups and instrumented traces were excluded. The evaluation requested ten results so it could measure nDCG@10; the sample requests up to three passages.

| Corpus | Mode | nDCG@10 | Precision@3 | Serial p95, ms |
|---|---|---:|---:|---:|
| NFCorpus | Balanced hybrid | 0.3557 | 0.3901 | 7.27 |
| NFCorpus | Semantic | 0.3476 | 0.3839 | 6.06 |
| NFCorpus | Lexical | 0.3262 | 0.3571 | 5.76 |
| ArguAna | Balanced hybrid | 0.4926 | 0.1638 | 19.97 |
| ArguAna | Semantic | 0.4829 | 0.1522 | 5.50 |
| ArguAna | Lexical | 0.4549 | 0.1496 | 16.20 |

The balanced hybrid scored slightly higher on both corpora and cost more search time. Neither nDCG difference establishes a reliable improvement: paired percentile bootstrap intervals for hybrid minus semantic were [-0.0046, +0.0211] on NFCorpus and [-0.0040, +0.0234] on ArguAna (95%, 10,000 resamples, seed 1729). These are previously observed test sets, not new holdouts. No settings were adjusted after this comparison. Local single-repeat timings exclude embedding generation and do not establish production capacity.

The averages include every query, including the five ArguAna queries whose judged positive document is missing from the corpus. Those five queries score zero. Identical-ID self matches are excluded as described in the [cross-corpus validation](vespa-native-validation.md#transfer-to-a-second-domain).

Configuration SHA-256: `cceb3f5dfbf279d524ed1abf2bcc079773be33f844774786f60b1face9a7d1a4`. Both runs used schema SHA-256: `9b1347dffa0ca25ef35458ca7c0bb2a9c40c7e42a26b61626e511808fe217eb1`.

Use a fresh dedicated instance for each corpus, prepare its embeddings as above, and select the saved comparison. For ArguAna:

```sh
dotnet build benchmarks/Vespa.NET.RetrievalEvaluation -c Release
dotnet benchmarks/Vespa.NET.RetrievalEvaluation/bin/Release/net10.0/Vespa.NET.RetrievalEvaluation.dll \
  --native --latency --dataset artifacts/retrieval-quality/arguana-test.json \
  --config benchmarks/Vespa.NET.RetrievalEvaluation/profiles/sample-default.json \
  --endpoint http://localhost:18086 --deploy --config-endpoint http://localhost:19076 --feed \
  --k 10 --repeats 1 --warmup 5 \
  --out artifacts/retrieval-quality/sample-default-arguana \
  --server-label 'record image digest, hardware and Docker limits'
uv run scripts/retrieval-latency.py \
  --dataset artifacts/retrieval-quality/arguana-test.json \
  --run artifacts/retrieval-quality/sample-default-arguana/latency-results.json \
  --reference semantic
```

For NFCorpus, use `nfcorpus-native-test.json` and a separate output directory on another fresh instance. Keep both the dataset and configuration hashes with the results when comparing future changes.

The [SciFact and FiQA transfer check](retrieval-transfer.md) extends this comparison without retuning the settings. It includes a substantial FiQA regression for the sample hybrid relative to semantic search; the earlier two-corpus improvement does not establish a general precision advantage.
