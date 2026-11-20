# Native Vespa validation: filters, matching and domain transfer

These measurements answer two questions about the [native retrieval sample](native-retrieval.md). How closely does an approximate search reproduce exact results as a filter gets narrower? And how well do weights selected on biomedical queries retrieve counterarguments in a second collection?

## HNSW with selective filters

The native evaluation schema builds an HNSW index on the real 768-dimensional Nomic embeddings, with 16 links per node and 200 insertion exploration neighbors. A deterministic SHA-256 bucket assigns documents to nested 100%, 10% and 1% scopes. The scope filter surrounds the whole lexical/vector union; every returned document is validated against it. The buckets simulate filters of different sizes.

On 2026-11-19 the complete NFCorpus test set ran through 24 scenarios: semantic and normalized hybrid, three scopes, exact search and ANN with 0/32/128 additional exploration hits. Every scenario includes all 323 queries, three measured repeats and five excluded warmups. There are 23,256 timed requests and 1,938 separate exact baseline requests. Profiling requests are separate from the latency measurements. The rank settings come from the frozen NFCorpus development configuration.

Representative measurements, k=10:

| Method | Scope | Documents | Requested search | Exact top-10 overlap | p95 ms | nDCG@10 |
|---|---:|---:|---|---:|---:|---:|
| Semantic | 100% | 3,633 | Exact | 1.0000 | 2.07 | 0.3474 |
| Semantic | 100% | 3,633 | ANN, extra=0 | 0.9916 | 2.17 | 0.3476 |
| Semantic | 100% | 3,633 | ANN, extra=128 | 0.9988 | 3.46 | 0.3477 |
| Hybrid | 100% | 3,633 | Exact | 1.0000 | 6.52 | 0.3607 |
| Hybrid | 100% | 3,633 | ANN, extra=0 | 0.9981 | 6.94 | 0.3598 |
| Hybrid | 10% | 376 | ANN, extra=0 | 1.0000 | 3.75 | 0.3187 |
| Hybrid | 1% | 45 | ANN requested, extra=0 | 1.0000 | 14.77 | 0.4193 |

All timed requests succeeded with complete coverage. The overlap metric uses all nonempty exact baselines across all repeats. Filtered relevance metrics use only queries with a known positive judgment inside the same scope: 323, 217 and 82 queries respectively. The report also records the omitted quality-query counts; latency and overlap still use all queries. Comparing filtered nDCG with unfiltered nDCG as if the query sets and retrieval task were identical would be misleading.

Supplemental execution plans for one query in every ANN scenario show `exact` at 1%, and `index top k using filter` at 10% and 100%, for both methods and all three exploration budgets. Vespa chose exact search for the most selective profiled requests even with approximation enabled. Only the supplemental requests have recorded plans. See the official [HNSW/filter guide](https://docs.vespa.ai/en/querying/approximate-nn-hnsw.html) and [query tracing reference](https://docs.vespa.ai/en/reference/api/query.html).

These serial requests ran on local Docker after warmup, with embeddings prepared in advance. Increasing exploration from 0 to 128 improved semantic top-10 overlap but raised p95. The scenarios ran in shuffled order (seed 1729). The 1% hybrid p95 comes from a single local run and cannot isolate the cost of exact search.

Each run records its schema hash and measures the exact reference on the same deployment as the approximate queries.

Reproduce on a dedicated instance, prepared using the quality guide:

```sh
dotnet build benchmarks/Vespa.NET.RetrievalEvaluation -c Release
dotnet benchmarks/Vespa.NET.RetrievalEvaluation/bin/Release/net10.0/Vespa.NET.RetrievalEvaluation.dll \
  --native --ann --dataset artifacts/retrieval-quality/nfcorpus-native-test.json \
  --config benchmarks/Vespa.NET.RetrievalEvaluation/profiles/nfcorpus-nomic.json \
  --endpoint http://localhost:18086 --deploy --config-endpoint http://localhost:19076 --feed \
  --scopes 100,10,1 --explore 0,32,128 --repeats 3 --warmup 5 \
  --out artifacts/retrieval-quality/native-ann \
  --server-label 'record the pinned image digest, hardware and Docker limits'
uv run scripts/ann-quality.py \
  --dataset artifacts/retrieval-quality/nfcorpus-native-test.json \
  --run artifacts/retrieval-quality/native-ann/ann-results.json
uv run scripts/ann-quality.py --self-test
```

The runner records query plans with `trace.explainLevel=2` and profile depth 4. To collect additional plans from an already prepared instance without remeasuring, add `--trace-only` to `--native --ann`, omit deployment/feed, and choose a fresh output directory. The report accepts `--plans path/to/ann-traces.json` and an optional fresh `--out` directory. It validates that supplemental plan metadata matches the measurement. Reports preserve all rankings, timings, plan algorithms, schema/data hashes and metric denominators, rejecting incomplete matrices.

Vespa image used for the measurements: `vespaengine/vespa@sha256:93268edd995b4e671be33babf7ebac55155fd0f330bb0b02123d1fbf3a9d1df0`.

## Native configuration and lexical regression checks

The live Agent Framework test deploys the actual sample application and verifies:

- `ZX-881`, `zx-881` and a near-miss against a cased identifier attribute.
- The full name `Giulia Rossi` with native phrase matching.
- `caffe` matching indexed `caffè` through normalization.
- Italian `gatto` matching indexed `gatti` with document and query language `it`.
- Ordered `pompa di calore` versus all-word `pompa calore` in a different order.
- Title-only versus body-only retrieval and a projected custom summary.
- A long quoted input with repeated terms using native `tokenize` grammar.

It also verifies lexical-relevant versus vector-nearer documents, unpublished document exclusion and integration through Microsoft's real `TextSearchProvider`, with deterministic test vectors and a deterministic chat client. This checks matching and framework integration; Italian semantic relevance still requires an appropriate embedding model and domain judgments.

```sh
dotnet test tests/Vespa.NET.AgentFramework.Tests -c Release
VESPA_AGENT_FRAMEWORK_INTEGRATION_TESTS=1 \
  VESPA_VECTOR_DATA_IMAGE=vespaengine/vespa:8.781.11 \
  dotnet test tests/Vespa.NET.AgentFramework.Tests -c Release \
  --filter FullyQualifiedName~LiveVespaTests
```

## Transfer to a second domain

ArguAna tests argument/counterargument retrieval rather than biomedical questions. The experiment uses all 8,674 documents and all 1,406 judged queries, with the same Nomic model digest, prefixes and explicit truncation policy. The actual model context is 2,048 tokens despite the requested 8,192; lexical indexing retains the full text. This matters for long arguments.

A quoted paragraph exceeded Vespa’s limit on repeated phrase terms under the original grammar. The [plain-text configuration](../benchmarks/Vespa.NET.RetrievalEvaluation/profiles/nfcorpus-nomic-plain-text.json) uses `tokenize` so quotes in an argument are treated as punctuation. This change preceded inspection of ArguAna scores. All weights, budgets and RRF constants come from the [NFCorpus development selection](../benchmarks/Vespa.NET.RetrievalEvaluation/profiles/nfcorpus-nomic.json). The table includes a fresh NFCorpus comparison with the same grammar.

Identical query/document IDs are excluded by a native fast-search attribute filter before both candidate branches, following [BEIR's evaluation convention](https://github.com/beir-cellar/beir/blob/main/beir/retrieval/evaluation.py). Five published positive judgments refer to document IDs missing from the source archive. Their IDs are recorded explicitly; all five impossible queries and all 1,406 total queries stay in every metric denominator. Missing or failed requests abort the run instead of shrinking that denominator.

All 5,624 ArguAna requests completed with full coverage. Self-document exclusion applied to 1,298 query IDs. Measured results using the plain-text protocol:

| Corpus | Method | nDCG@10 | Recall@10 | MRR@10 | Precision@3 |
|---|---|---:|---:|---:|---:|
| NFCorpus | Lexical | 0.3262 | 0.1582 | 0.5284 | 0.3571 |
| NFCorpus | Semantic | 0.3474 | 0.1722 | 0.5335 | 0.3839 |
| NFCorpus | Normalized hybrid | 0.3600 | 0.1733 | 0.5672 | 0.4014 |
| NFCorpus | RRF | 0.3508 | 0.1688 | 0.5501 | 0.3922 |
| ArguAna | Lexical | 0.4549 | 0.7425 | 0.3653 | 0.1491 |
| ArguAna | Semantic | 0.4834 | 0.7767 | 0.3932 | 0.1517 |
| ArguAna | Normalized hybrid | 0.5084 | 0.8129 | 0.4140 | 0.1679 |
| ArguAna | RRF | 0.4994 | 0.8065 | 0.4044 | 0.1641 |

On ArguAna, normalized hybrid increases observed nDCG by 0.0250, or about 5.2% relative to semantic retrieval, with 403 query wins, 287 losses and 716 ties. Its mean search time is 115.35 ms versus semantic's 5.58 ms; these exact-search, serial timings include cold requests and exclude embeddings. The [latency guide](retrieval-latency.md) shows how candidate budgets and weakAnd matching affected these long queries.

The prepared ArguAna file SHA-256 is `655f40bcadfe5fbacb35cae1091222a6b17a25fa7963eb28c11f6386d6e7c86c`. To reproduce the transfer comparison, prepare ArguAna and run the plain-text profile on a separate instance.

```sh
curl -fsSL https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/arguana.zip \
  -o artifacts/retrieval-quality/arguana.zip
uv run scripts/retrieval-quality.py prepare --corpus arguana \
  --archive artifacts/retrieval-quality/arguana.zip --split test --truncate \
  --out artifacts/retrieval-quality/arguana-test.json

# Start a separate, empty Vespa instance on these unused ports first.
dotnet benchmarks/Vespa.NET.RetrievalEvaluation/bin/Release/net10.0/Vespa.NET.RetrievalEvaluation.dll \
  --native --dataset artifacts/retrieval-quality/arguana-test.json \
  --config benchmarks/Vespa.NET.RetrievalEvaluation/profiles/nfcorpus-nomic-plain-text.json \
  --endpoint http://localhost:18087 --deploy --config-endpoint http://localhost:19077 --feed \
  --out artifacts/retrieval-quality/arguana-frozen \
  --server-label 'record the pinned image and hardware; frozen ranking with tokenize parsing'
uv run scripts/retrieval-quality.py report \
  --dataset artifacts/retrieval-quality/arguana-test.json \
  --run artifacts/retrieval-quality/arguana-frozen/results.json
```
