# Does E5 close the hybrid retrieval gap on FiQA?

E5 improved both FiQA scores, but semantic search gained more. The balanced hybrid rose from 0.3159 to 0.3258 nDCG@10. Semantic search alone rose from 0.3706 to 0.3946. Changing the embedding pipeline left hybrid behind on this collection. The sample defaults to semantic search with Nomic embeddings.

| Corpus | Embeddings | Semantic nDCG@10 | Hybrid nDCG@10 | Semantic Precision@3 | Hybrid Precision@3 |
|---|---|---:|---:|---:|---:|
| FiQA | Nomic | 0.3706 | 0.3159 | 0.2243 | 0.1878 |
| FiQA | E5 | 0.3946 | 0.3258 | 0.2361 | 0.1950 |
| SciFact | Nomic | 0.7028 | 0.7214 | 0.2556 | 0.2589 |
| SciFact | E5 | 0.7195 | 0.7248 | 0.2722 | 0.2567 |

On SciFact, the balanced hybrid ranked slightly higher than semantic search by nDCG@10 with either model. With E5, semantic search had the higher Precision@3, the cutoff closest to the sample's maximum of three passages. The frozen ArguAna quality preset reached 0.7466 nDCG@10 on SciFact and 0.3999 on FiQA with E5, using broader candidate budgets and different fusion settings. All five configurations used the settings from the [Nomic comparison](retrieval-transfer.md). Neither collection was new to the evaluation, and no settings were retuned. An MS MARCO evaluation with these configurations is pending.

## How much did the gap change?

For each query, average nDCG@10 over the three repeats and subtract the semantic score from the balanced hybrid score. The last column subtracts that same query's Nomic gap from its E5 gap. Intervals are 95% percentile bootstrap intervals from 10,000 resamples of queries within each corpus (seed 1729), without adjustment for multiple comparisons.

| Corpus | Hybrid minus semantic with E5 (95% interval) | Change in gap from Nomic to E5 (95% interval) |
|---|---:|---:|
| FiQA | -0.0688 [-0.0913, -0.0464] | -0.0141 [-0.0323, +0.0044] |
| SciFact | +0.0054 [-0.0227, +0.0339] | -0.0132 [-0.0378, +0.0110] |

The FiQA gap remained below zero with E5. Its observed widening was 0.0141, but the interval for the change includes zero. The comparison also includes a tokenizer change, a shorter context window and an HNSW index built from new vectors. The Nomic run requested 8,192 tokens from Ollama. Runtime logs show that the GGUF model's effective context was capped at 2,048. Only the E5 run measured input truncation counts.

## What stayed comparable

For each corpus, canonical hashes matched across pipelines for the source fields, IDs, query text and relevance judgments. Both deployments used schema SHA-256 `9b1347dffa0ca25ef35458ca7c0bb2a9c40c7e42a26b61626e511808fe217eb1`.

Lexical search uses no query vector. Its top ten were identical across all three repeats for every SciFact query and 647 of 648 FiQA queries. Only the last two places for FiQA query 2747 differed, with no change to the lexical relevance metrics. On the same E5 index, a traced weakAnd query with target 100 reproduced the E5 ranking. Targets 1,000 and 100,000 reproduced the Nomic top ten, including document 215437 with BM25 score 22.06483195. The largest target served as a reference. The probe shows that candidate selection can change which hits reach BM25 ranking. Why the two fresh indexes differed at the original target remains open. [Vespa weakAnd matching](https://docs.vespa.ai/en/ranking/wand.html)

## Prepare the embeddings

Run from the repository root. Download the six files listed in the [pinned model manifest](../benchmarks/Vespa.NET.RetrievalEvaluation/models/e5-base-v2.json) (the weights are about 438 MB). The preparer verifies file sizes and SHA-256 hashes before loading the tokenizer and model locally, with remote code disabled. PyTorch 2.15.0 and Transformers 5.23.0 are Python dependencies for this optional benchmark. The weights and generated files live under `artifacts/`. Vespa serves precomputed vectors without an ONNX deployment. The SDK and sample keep their existing embedding configuration.

```sh
base=artifacts/e5-reproduction
model_dir="$base/model"
manifest=benchmarks/Vespa.NET.RetrievalEvaluation/models/e5-base-v2.json
mkdir -p "$model_dir"
python3 - "$manifest" "$model_dir" <<'PY'
import json
from pathlib import Path
import subprocess
import sys

manifest, directory = map(Path, sys.argv[1:])
for name, entry in json.loads(manifest.read_text())["files"].items():
    subprocess.run(["curl", "-fsSL", entry["url"], "-o", str(directory / name)], check=True)
PY

for corpus in scifact fiqa; do
  mkdir -p "$base/$corpus"
  source_manifest="benchmarks/Vespa.NET.RetrievalEvaluation/datasets/$corpus-source.json"
  uv run scripts/retrieval-quality.py fetch \
    --source-manifest "$source_manifest" --out "$base/$corpus.zip"
  batch=16
  if [ "$corpus" = fiqa ]; then batch=32; fi
  uv run scripts/prepare-e5.py --corpus "$corpus" --archive "$base/$corpus.zip" \
    --source-manifest "$source_manifest" --source-url "https://huggingface.co/datasets/BeIR/$corpus" \
    --model-dir "$model_dir" --batch-size "$batch" --out "$base/$corpus/prepared.json"
done
```

The manifest pins E5 revision `f52bf8ec8c7124536f0efb74aca902b2995e5bcd`. Each query starts with `query: ` and each document with `passage: `, including the trailing space. Prefixes and special tokens count toward the 512-token limit. The preparer averages token vectors over the attention mask, including special tokens and excluding padding, then L2-normalizes the 768-dimensional FP32 result. BM25 indexes the complete prepared text.

[E5's model configuration](https://huggingface.co/intfloat/e5-base-v2/blob/f52bf8ec8c7124536f0efb74aca902b2995e5bcd/config.json) sets `max_position_embeddings` to 512. Raising a tokenizer limit alone cannot extend the model's input window. The benchmark truncated 466 of 5,183 SciFact papers, including 47 of the 283 distinct papers judged relevant. On FiQA it truncated 2,446 of 57,638 answers, including 131 of 1,706 distinct answers judged relevant. All 300 SciFact queries and 648 FiQA queries fit. Chunking long documents would change what gets indexed and retrieved and would require a separate evaluation.

Preparation on Apple MPS used maximum batch sizes of 16 for SciFact and 32 for FiQA. The recorded batch size is the invocation's maximum; cached vectors can come from earlier batches. On two startup test inputs, the largest absolute difference between MPS and CPU vector components was 1.54 × 10⁻⁷. The padding check gave 8.68 × 10⁻⁸.

## Run the five configurations

Finish preparing both collections before timing. Use a fresh dedicated instance for each corpus with Vespa image `vespaengine/vespa@sha256:93268edd995b4e671be33babf7ebac55155fd0f330bb0b02123d1fbf3a9d1df0`. The following command deploys the benchmark application and feeds SciFact. Repeat with `corpus=fiqa` against the second instance. Record hardware and container limits in the server label.

```sh
base=artifacts/e5-reproduction
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

The configuration file has SHA-256 `f904e2bdefc0be62f80c76748cad8ced6c9d2ac507ddc4bb1ca223a89903d7b1`. It includes the sample hybrid, lexical and semantic modes and both ArguAna presets. The benchmark requests ten hits for scoring and excludes ten warmups per configuration and separate instrumented traces. Serial search latency excludes embedding generation. The sample requests up to three passages.
