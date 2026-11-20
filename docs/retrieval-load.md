# Validate optimized retrieval under filters and concurrent load

How does a narrower filter affect relevance and latency when several searches run at once? This benchmark runs the same [two hybrid presets](retrieval-latency.md) at three filter sizes and three concurrency levels. Each worker submits its next query as soon as its previous request finishes. The serial case provides a control for each preset and filter.

## What is measured

Each preset runs with deterministic hash-bucket scopes of 100%, 10% and 1%, and closed-loop concurrency 1, 4 and 8. Scope membership is independent of relevance judgments. The outer filter applies to the entire vector/text union; every returned ID is checked against scope membership and self-document exclusion. An identity or scope violation aborts the run.

The matrix has 18 cases per corpus. Every case includes every query and repeat. Case order is shuffled with seed 1730; query order is shuffled with seed 1729 and held identical across cases. All workers in one case share a native client. The next case starts after the previous case has drained completely.

Warmups and separate profiled requests are excluded from measurements. Request timers include request construction, server execution, summaries and response validation. They exclude embeddings and scheduling before a worker slot is available. Throughput uses monotonic elapsed time for the entire measured case, including its final drain. This reports achieved closed-loop throughput, not maximum service capacity, fixed-arrival latency or external queueing behavior.

Transport/server errors, request timeouts and incomplete/degraded coverage are recorded as failed attempts with empty hits. They remain in latency, throughput and quality denominators. The caller's cancellation still aborts the experiment. Warmup failures are counted separately. The offline report rejects missing or duplicated attempts, inconsistent case totals, unknown documents and scope leaks.

## Which queries count toward quality?

Each comparison pairs the same query and repeat across concurrency levels.

- Conditional nDCG uses only queries with a known positive judgment among
  eligible documents. Both IDCG and returned rankings use the same scope.
- All-query nDCG is a zero-extended diagnostic: queries without an eligible
  positive receive zero. Its denominator includes every query, including
  unavailable source positives. Failed requests also receive zero.
- ID overlap recovers the serial IDs using the actual serial ranking length.
  Empty or failed serial rankings are excluded from overlap only, with explicit
  counts. They remain in the other metrics.

Conditional nDCG across different scopes describes different query sets. The all-query diagnostic across scopes additionally changes how many questions have an admissible answer. Neither should be presented as an isolated precision gain from a more selective filter. ArguAna has one published positive per query, so the 1% scope can contain very few informative queries.

The reporter aggregates repeats per query and computes relevance with `ir_measures==0.4.3`.

## Reproduce

Prepare NFCorpus as described in the [quality guide](retrieval-quality.md#tune-the-native-sample). Use a dedicated Vespa instance. The setup command requires `jq`. It combines the two checked-in presets into the configuration file the load runner expects.

```sh
mkdir -p artifacts/load-validation
jq -s '{configurations: map(.selected)}' \
  benchmarks/Vespa.NET.RetrievalEvaluation/profiles/arguana-nomic-quality.json \
  benchmarks/Vespa.NET.RetrievalEvaluation/profiles/arguana-nomic-latency.json \
  > artifacts/load-validation/configurations.json

dotnet build benchmarks/Vespa.NET.RetrievalEvaluation -c Release
dotnet benchmarks/Vespa.NET.RetrievalEvaluation/bin/Release/net10.0/Vespa.NET.RetrievalEvaluation.dll \
  --native --load --dataset artifacts/retrieval-quality/nfcorpus-native-test.json \
  --config artifacts/load-validation/configurations.json \
  --endpoint http://localhost:18086 --deploy --config-endpoint http://localhost:19076 --feed \
  --scopes 100,10,1 --concurrency 1,4,8 --repeats 2 --warmup 16 \
  --timeout-minutes 60 --out artifacts/load-validation/nf \
  --server-label 'record pinned image digest, hardware and Docker limits'

uv run scripts/retrieval-load.py \
  --dataset artifacts/retrieval-quality/nfcorpus-native-test.json \
  --run artifacts/load-validation/nf/load-results.json
uv run scripts/retrieval-load.py --self-test
```

`--load` requires explicit normalized-hybrid ANN presets and a concurrency-1 control. The runner accepts up to 64 active workers, but this study measures only 1/4/8. An already prepared dedicated instance can be reused by omitting deployment/feed. Outputs require a fresh directory. `--trace-only` records diagnostics without measuring the full matrix.

The 50th/90th query-length diagnostics run separately for every preset and scope. They record actual execution-plan algorithms; `approximate: true` permits Vespa to choose exact search for a highly selective filter. See the official [filtered HNSW guide](https://docs.vespa.ai/en/querying/approximate-nn-hnsw.html) and [query/coverage reference](https://docs.vespa.ai/en/reference/api/query.html).

## NFCorpus measurement

The complete matrix ran with 323 queries and two repeats: 11,628 measured attempts, plus excluded warmups/profiles. Every attempt succeeded. For both presets and every scope, concurrency 4 and 8 recovered all serial result IDs, with zero nDCG delta against their same-preset serial controls.

| Preset | Full-scope concurrency | nDCG@10 | Valid responses/s | p95 ms |
|---|---:|---:|---:|---:|
| Quality | 1 | 0.3618 | 277.30 | 4.95 |
| Quality | 4 | 0.3618 | 577.90 | 15.82 |
| Quality | 8 | 0.3618 | 653.07 | 20.81 |
| Latency | 1 | 0.3610 | 236.88 | 7.27 |
| Latency | 4 | 0.3610 | 983.30 | 13.71 |
| Latency | 8 | 0.3610 | 1448.38 | 14.54 |

Actual scope cardinalities were 3,633 / 376 / 45 documents before self-exclusion; conditional quality used 323 / 217 / 82 queries. The profiled 100% and 10% requests used `index top k using filter`; the 1% requests used `exact`. Even at concurrency 1, the smaller preset had the higher p95 in this local run.

The example command writes all 18 cases, including rankings, errors and coverage, to the requested output directory.

## ArguAna measurement

The same 18 cases ran on all 1,406 paragraph queries with one repeat: 25,308 measured attempts. Every attempt succeeded, and concurrent cases recovered all same-preset serial IDs with zero nDCG delta at every scope. Together the two corpora account for 36,936 completed measured attempts.

| Preset | Full-scope concurrency | All-query nDCG@10 | Valid responses/s | p95 ms |
|---|---:|---:|---:|---:|
| Quality | 1 | 0.5101 | 45.97 | 45.89 |
| Quality | 4 | 0.5101 | 134.39 | 50.57 |
| Quality | 8 | 0.5101 | 127.67 | 125.28 |
| Latency | 1 | 0.5072 | 85.87 | 23.69 |
| Latency | 4 | 0.5072 | 188.07 | 38.77 |
| Latency | 8 | 0.5072 | 328.28 | 47.09 |

For these local observations, raising the broader preset from concurrency 4 to 8 increased p95 without increasing achieved throughput. The smaller preset handled concurrency 8 at a lower p95 and higher throughput, with its measured relevance tradeoff. Serial p95 values from the earlier study should not be used as concurrent-load guarantees.

Actual scope cardinalities were 8,674 / 841 / 93 documents before self-exclusion, with 1,401 / 123 / 14 queries containing an eligible known positive. All 1,406 queries remained in the all-query diagnostic, including the five unavailable source positives. Conditional nDCG at 1% was 1.0 on only 14 informative queries; the other 1,392 queries had no eligible known positive. Diagnostic 100/10% requests used the index, while 1% requests used exact search for both presets.
