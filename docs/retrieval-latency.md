# Profile native retrieval and compare quality with latency

On ArguAna, some queries are entire arguments. The [exact-search comparison](vespa-native-validation.md#transfer-to-a-second-domain) averaged 115 ms per hybrid query. To find where that time went, the benchmark profiles short, median and long queries, then varies three settings on the same index: vector search (exact or HNSW), candidate and reranking budgets, and weakAnd term pruning. The timed CPU queries use precomputed embeddings.

## Where Vespa spent time

The tables distinguish average latency from p95: the time within which 95% of the measured requests finished. This shows slower responses that an average can hide. Relevance is measured separately with nDCG@10, as described in the [quality guide](retrieval-quality.md#read-the-quality-report).

On fixed 10th/50th/90th query-length quantiles (107, 174 and 299 whitespace words), the main cost was matching, especially non-strict weakAnd traversal. The table shows one profile of the 174-word query.

| Configuration | Matching ms | First-phase ranking ms | Mixed first-phase evaluations |
|---|---:|---:|---:|
| Exact, targets 600/300, rerank 900 | 156.35 | 2.31 | 2,649 |
| Exact, targets 600/300, rerank 100 | 136.47 | 1.67 | 2,649 |
| Exact, targets 100/100, rerank 100 | 113.76 | 0.78 | 887 |
| HNSW, targets 600/300, rerank 900 | 29.48 | 1.55 | 1,724 |
| HNSW, targets 100/100, rerank 100 | 18.95 | 0.80 | 656 |

Reducing the rerank budget from 900 to 100 left all 2,649 first-phase evaluations in place. Switching to HNSW at the original budgets cut matching time from 156.35 ms to 29.48 ms in this trace. The untraced measurements below give per-query latency across the corpus.

The execution plans also show a change in iterator order. In the exact query, the nearest-neighbor/lexical union's estimate covers essentially the whole corpus. The outer scope attribute drives the scan and weakAnd is non-strict. In the HNSW query the vector branch has a smaller estimate; the union drives matching with strict weakAnd. HNSW therefore changes both vector retrieval and how Vespa executes the combined lexical query. Scope filters remain in place throughout. The benchmark's run identifier covers the whole dedicated corpus; selective production scopes can lead to a different plan and cost.

Exact nearest-neighbor and weakAnd targets are not strict limits on documents exposed to first-phase ranking. The observed counts above exceed their target sums. See [Vespa's hybrid tutorial](https://docs.vespa.ai/en/learn/tutorials/hybrid-search.html), [phased ranking](https://docs.vespa.ai/en/ranking/phased-ranking.html) and [execution profiling](https://docs.vespa.ai/en/reference/api/query.html).

## Reproduce the comparison

The [study configurations](../benchmarks/Vespa.NET.RetrievalEvaluation/profiles/arguana-latency-study.json) list the candidate budgets, reranking depths and pruning settings for each comparison. The latency runner honors each configuration’s `approximate` setting. Use `false` for exact search.

Use a dedicated empty instance, the prepared ArguAna file, and the same pinned image as the [quality guide](retrieval-quality.md). For the complete exploratory matrix:

```sh
dotnet build benchmarks/Vespa.NET.RetrievalEvaluation -c Release
dotnet benchmarks/Vespa.NET.RetrievalEvaluation/bin/Release/net10.0/Vespa.NET.RetrievalEvaluation.dll \
  --native --latency --dataset artifacts/retrieval-quality/arguana-test.json \
  --config benchmarks/Vespa.NET.RetrievalEvaluation/profiles/arguana-latency-study.json \
  --endpoint http://localhost:18087 --deploy --config-endpoint http://localhost:19077 --feed \
  --repeats 1 --warmup 10 --timeout-minutes 90 \
  --out artifacts/retrieval-quality/latency-sweep \
  --server-label 'record image digest, hardware and Docker limits'

uv run scripts/retrieval-latency.py \
  --dataset artifacts/retrieval-quality/arguana-test.json \
  --run artifacts/retrieval-quality/latency-sweep/latency-results.json
uv run scripts/retrieval-latency.py --self-test
```

Omit deployment/feed to reuse the unchanged dedicated instance. A new output directory is required. `--trace-only` captures the fixed query-length diagnostics without timing the full matrix. Each configuration produces its native trace; missing traces, backend errors, incomplete coverage or invalid result identities fail the run. All data and result rows must match the same prepared-file hash.

Each measured repeat shuffles query order and interleaves configurations with seed 1729. Result projection and summaries are identical. Timers cover native request construction through response validation, including summary transfer, and exclude embeddings, warmups and instrumented diagnostics. The reporter requires every query/configuration/repeat, preserves empty rankings and includes the five unavailable ArguAna positives in every metric denominator. Quality is computed separately per repeat and then averaged; repeated queries are not treated as independent judgments.

Reports contain mean/p50/p95, nDCG, precision, recall, per-query paired deltas, top-10 overlap and profile summaries. The overlap denominator is the number of returned reference IDs, omitting only empty reference rankings and recording their count.

The sweep reused a previously observed test set. To qualify for a second run, a configuration needed a score within 0.005 mean nDCG@10 of the current reference and a lower observed p95. The second run tests repeatability on those same queries.

## ArguAna candidate and reranking budgets

The sweep completed 12,654 timed requests (nine configurations for every one of the 1,406 queries), with ten excluded warmups per configuration. All nine configurations used the same schema and instance. Seven are shown below.

| Configuration | Vector/lexical targets | Global rerank | ANN | Native pruning | nDCG@10 | Mean ms | p95 ms |
|---|---|---:|---|---|---:|---:|---:|
| Reference | 600/300 | 900 | No | Unset | 0.5084 | 68.89 | 136.37 |
| Rerank only | 600/300 | 100 | No | Unset | 0.5057 | 60.04 | 120.74 |
| Small exact | 100/100 | 100 | No | Unset | 0.5055 | 52.42 | 106.36 |
| Same-budget HNSW | 600/300 | 900 | Yes | Unset | 0.5085 | 23.33 | 41.80 |
| Small HNSW | 100/100 | 100 | Yes | Unset | 0.5060 | 10.31 | 19.21 |
| Native pruning | 600/300 | 900 | Yes | 0.6 / 0.01 | 0.5102 | 18.17 | 32.69 |
| Medium pruning | 200/100 | 100 | Yes | 0.6 / 0.01 | 0.5073 | 8.80 | 18.12 |

Pruning columns are `weakAndStopwordLimit` / `weakAndAdjustTarget`. The pair comes from the official serving-tuning example; this experiment does not separately attribute their effects. Same-budget HNSW recovers 99.95% of reference top-ten IDs in this sweep. Of the configurations that qualified for a second run, the benchmark kept the one with the highest nDCG and the one with the lowest p95.

## Three-repeat confirmation

The two selected configurations and three controls completed 21,090 timed requests across all 1,406 queries in three repeats. The p95 reductions use the exact hybrid reference from this run.

| Configuration | nDCG@10 | Precision@3 | Mean ms | p95 ms | p95 reduction |
|---|---:|---:|---:|---:|---:|
| Semantic exact | 0.4834 | 0.1517 | 3.11 | 4.64 | — |
| Hybrid exact reference | 0.5084 | 0.1679 | 68.23 | 133.44 | — |
| Same-budget HNSW | 0.5085 | 0.1679 | 22.74 | 40.11 | 69.9% |
| HNSW, native pruning, 600/300/900 | 0.5102 | 0.1686 | 17.51 | 31.67 | 76.3% |
| HNSW, native pruning, 200/100/100 | 0.5073 | 0.1681 | 8.33 | 16.43 | 87.7% |

The observed nDCG differences from the hybrid exact reference are +0.0018 and −0.0011 for the two pruned profiles. These are descriptive differences, without a claim of statistical superiority or equivalence. The semantic-only control is still faster but has lower observed relevance on this dataset.

The sample can load either preset directly.

- [Quality priority](../benchmarks/Vespa.NET.RetrievalEvaluation/profiles/arguana-nomic-quality.json):
  preserves 600 vector / 300 lexical targets and global depth 900.
- [Latency priority](../benchmarks/Vespa.NET.RetrievalEvaluation/profiles/arguana-nomic-latency.json):
  uses 200 vector / 100 lexical targets and global depth 100.

Both use approximation, weakAnd stopword limit 0.6 and adjustment target 0.01, with unchanged vector weight 0.75 and title weight 1. The sample reads the `selected` object; metadata records the selection protocol, hashes and measured results. Each file also carries semantic and exact-hybrid controls for evaluation. They remain corpus-specific opt-in choices. Sample usage:

```sh
dotnet run --project samples/Vespa.NET.AgentFramework -- \
  --retrieval-config benchmarks/Vespa.NET.RetrievalEvaluation/profiles/arguana-nomic-quality.json \
  "Find the relevant documentation"
```

This command changes the retrieval settings on the sample’s existing passages.

## Check the same profiles on NFCorpus

The benchmark ran the same five configurations on all 323 NFCorpus test queries with three repeats on a fresh dedicated instance. No parameters changed after the ArguAna confirmation. This adds 4,845 timed requests and checks transfer back to the original domain; both corpora had already been observed in this project.

| Configuration | nDCG@10 | Precision@3 | Mean ms | p95 ms |
|---|---:|---:|---:|---:|
| Semantic exact | 0.3473 | 0.3839 | 2.57 | 3.89 |
| Hybrid exact reference | 0.3600 | 0.4014 | 4.65 | 6.60 |
| Same-budget HNSW | 0.3591 | 0.4014 | 4.49 | 6.52 |
| HNSW, native pruning, 600/300/900 | 0.3618 | 0.4056 | 4.12 | 6.11 |
| HNSW, native pruning, 200/100/100 | 0.3610 | 0.4025 | 2.91 | 4.38 |

The two pruned profiles have observed nDCG deltas of +0.0017 and +0.0009 versus this deployment's exact reference, with no retuning. The performance gain is smaller on NFCorpus's short queries than on ArguAna's paragraphs. The much lower ID overlap of the pruned profiles (about 87% and 86.5%) also shows why matching the exact result list is not the same metric as relevance against judgments. Both preset files include the NFCorpus and ArguAna measurements.

For latency with several requests in flight, see the [filtered load measurements](retrieval-load.md). Those runs have their own concurrency-1 control for each preset and scope.
