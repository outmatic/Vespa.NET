# /// script
# requires-python = ">=3.11,<3.14"
# dependencies = ["torch==2.15.0", "transformers==5.23.0", "ir-measures==0.4.3"]
# ///
"""Prepare a BEIR corpus with a pinned local E5 model for the retrieval benchmark."""

import argparse
import hashlib
import importlib.metadata
import importlib.util
import json
import math
from pathlib import Path
import sys

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("quality", Path(__file__).with_name("retrieval-quality.py"))
quality = importlib.util.module_from_spec(spec)
spec.loader.exec_module(quality)


def file_sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def embedding_factory(args):
    # Heavy dependencies are needed only for explicit model preparation.
    import torch
    import torch.nn.functional as functional
    from transformers import AutoModel, AutoTokenizer

    manifest_bytes = args.model_manifest.read_bytes()
    manifest = json.loads(manifest_bytes)
    quality.require(manifest["model"] == "intfloat/e5-base-v2" and manifest["dimensions"] == 768
                    and manifest["maxTokens"] == 512, "This experiment uses the pinned E5-base-v2 recipe.")
    for name, record in manifest["files"].items():
        quality.require(Path(name).name == name, "Expected a model filename without path components.")
        path = args.model_dir / name
        quality.require(path.is_file() and path.stat().st_size == record["bytes"]
                        and file_sha256(path) == record["sha256"], "Model file checksum mismatch: " + name)
    torch.set_num_threads(4)
    device = args.device
    if device == "auto":
        device = "mps" if torch.backends.mps.is_available() else "cpu"
    quality.require(device != "mps" or torch.backends.mps.is_available(), "MPS is unavailable; use --device cpu.")
    tokenizer = AutoTokenizer.from_pretrained(str(args.model_dir), local_files_only=True, trust_remote_code=False)
    model = AutoModel.from_pretrained(str(args.model_dir), local_files_only=True, trust_remote_code=False,
                                      use_safetensors=True).to(dtype=torch.float32).eval()
    quality.require(model.config.hidden_size == 768, "Expected 768-dimensional E5 embeddings.")

    def encode(texts, target):
        batch = tokenizer(texts, max_length=512, padding=True, truncation=True, return_tensors="pt")
        batch = {name: value.to(target) for name, value in batch.items()}
        with torch.inference_mode():
            hidden = model(**batch).last_hidden_state.float()
            hidden = hidden.masked_fill(~batch["attention_mask"][..., None].bool(), 0.0)
            pooled = hidden.sum(dim=1) / batch["attention_mask"].sum(dim=1)[..., None]
            return functional.normalize(pooled, p=2, dim=1).cpu()

    # Compare the device path with CPU and check padding before encoding the corpus.
    examples = ["query: How is a schema deployed?", "passage: " + "Text and vectors support retrieval. " * 100]
    reference = encode(examples, "cpu")
    model.to(device)
    actual = encode(examples, device)
    alone = encode(examples[:1], device)
    device_delta = (actual - reference).abs().max().item()
    padding_delta = (actual[:1] - alone).abs().max().item()
    quality.require(device_delta < 1e-4 and padding_delta < 1e-4,
                    "E5 device/padding results differ from the FP32 CPU reference.")
    quality.require(torch.allclose(actual.norm(dim=1), torch.ones(len(examples)), atol=1e-5),
                    "Expected L2-normalized E5 vectors.")
    versions = {name: importlib.metadata.version(name) for name in ["torch", "transformers", "tokenizers", "safetensors"]}
    identity = {"model": manifest["model"], "revision": manifest["revision"],
                "digest": manifest["files"]["model.safetensors"]["sha256"],
                "modelManifestSha256": hashlib.sha256(manifest_bytes).hexdigest(),
                "truncate": True, "numCtx": 512, "pooling": manifest["pooling"], "normalize": "L2",
                "dtype": "float32", "device": device, "runtime": versions}
    print(f"E5 ready on {device}: CPU max delta={device_delta:.2g}, padding max delta={padding_delta:.2g}", flush=True)

    def generate(texts):
        vectors = encode(texts, device).tolist()
        quality.require(all(quality.vector_valid(v, 768) and math.isclose(sum(x * x for x in v), 1, abs_tol=1e-4)
                            for v in vectors), "Invalid or unnormalized E5 embeddings.")
        return vectors

    def describe(corpus):
        # Count all inputs, including cache hits, with the model's tokenizer.
        positive_ids = {identifier for query in corpus["queries"] for identifier, grade in query["relevance"].items() if grade > 0}

        def lengths(items, prefix):
            result = {}
            for start in range(0, len(items), 256):
                batch = items[start:start + 256]
                encoded = tokenizer([prefix + item["text"] for item in batch], truncation=False, padding=False,
                                    return_length=True, verbose=False)
                result.update((item["id"], length) for item, length in zip(batch, encoded["length"]))
            return result

        documents = lengths(corpus["documents"], args.document_prefix)
        queries = lengths(corpus["queries"], args.query_prefix)
        positive_lengths = [length for identifier, length in documents.items() if identifier in positive_ids]

        def summarize(values):
            values = list(values)
            return {"count": len(values), "overLimit": sum(n > 512 for n in values),
                    "maxTokensBeforeTruncation": max(values, default=0)}

        return {"maxTokensIncludesPrefixAndSpecialTokens": 512,
                "documents": summarize(documents.values()), "queries": summarize(queries.values()),
                "uniquePositiveDocuments": summarize(positive_lengths),
                "batchSize": args.batch_size, "batchOrder": "ascending input character count",
                "cpuReferenceMaxAbsDifference": device_delta, "paddingMaxAbsDifference": padding_delta}

    return identity, generate, describe


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", choices=["scifact", "fiqa"], required=True)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--source-manifest", type=Path, required=True)
    parser.add_argument("--source-url")
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--model-dir", type=Path, required=True)
    parser.add_argument("--model-manifest", type=Path,
                        default=Path(__file__).resolve().parents[1] / "benchmarks/Vespa.NET.RetrievalEvaluation/models/e5-base-v2.json")
    parser.add_argument("--batch-size", type=int, default=16)
    parser.add_argument("--device", choices=["auto", "cpu", "mps"], default="auto")
    parser.set_defaults(split="test", query_limit=None, ignore_identical_ids=False, context_length=512, truncate=True,
                        document_prefix="passage: ", query_prefix="query: ", sort_by_length=True)
    quality.prepare(parser.parse_args(), embedding_factory)


if __name__ == "__main__":
    main()
