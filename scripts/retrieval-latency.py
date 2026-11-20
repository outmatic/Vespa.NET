# /// script
# requires-python = ">=3.11,<3.14"
# dependencies = ["ir-measures==0.4.3"]
# ///
"""Report complete native retrieval quality/latency experiments; never contacts a service."""

import argparse
from copy import deepcopy
import hashlib
import importlib.metadata
import importlib.util
import json
import math
from pathlib import Path
import statistics
import sys

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("quality", Path(__file__).with_name("retrieval-quality.py"))
quality = importlib.util.module_from_spec(spec)
spec.loader.exec_module(quality)
require = quality.require


def percentile(values, quantile):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * quantile) - 1)]


def finite_number(value):
    return type(value) in (int, float) and math.isfinite(value)


def validate(corpus, run, digest, reference):
    quality.validate_corpus(corpus)
    require(run["datasetSha256"] == digest, "Dataset/run hash mismatch.")
    require(type(run["k"]) is int and run["k"] > 0 and type(run["repeats"]) is int and run["repeats"] > 0, "Invalid cutoff/repeats.")
    names = [c["name"] for c in run["configurations"]]
    require(names and len(set(names)) == len(names) and reference in names, "Missing/duplicate configurations or reference.")
    require(all(type(c["approximate"]) is bool for c in run["configurations"]), "Record approximation for every configuration.")
    queries = {q["id"]: q for q in corpus["queries"]}
    docs = {d["id"] for d in corpus["documents"]}
    expected = {(q, mode, repeat) for q in queries for mode in names for repeat in range(run["repeats"])}
    seen = set()
    for row in run["results"]:
        require(type(row["repeat"]) is int, "Repeat indices must be integers, not booleans or floats.")
        key = (row["queryId"], row["mode"], row["repeat"])
        require(key in expected and key not in seen, "Unknown/duplicate measurement.")
        seen.add(key)
        require(finite_number(row["milliseconds"]) and row["milliseconds"] >= 0, "Invalid latency.")
        hits = row["hits"]
        require(len(hits) <= run["k"] and len({h["id"] for h in hits}) == len(hits), "Duplicate/excess hits.")
        require(all(h["id"] in docs and h["id"] != queries[row["queryId"]].get("excludeDocumentId")
                    and finite_number(h["score"]) for h in hits), "Invalid identity, exclusion or score.")
    require(seen == expected, "Incomplete matrix: every query/configuration/repeat must be present, including empty results.")


def calculate(corpus, run, reference):
    names = [c["name"] for c in run["configurations"]]
    repeated, per_query = [], {}
    for repeat in range(run["repeats"]):
        rows = [r for r in run["results"] if r["repeat"] == repeat]
        metrics, queries = quality.calculate(corpus, {"modes": names, "k": run["k"], "results": rows})
        repeated.append(metrics)
        per_query[str(repeat)] = queries
    indexed = {(r["queryId"], r["mode"], r["repeat"]): r for r in run["results"]}
    metric = f"nDCG@{run['k']}"
    output = []
    for mode in names:
        rows = [r for r in run["results"] if r["mode"] == mode]
        overlap = []
        for row in rows:
            baseline = indexed[(row["queryId"], reference, row["repeat"])]["hits"]
            if baseline:
                overlap.append(len({h["id"] for h in baseline} & {h["id"] for h in row["hits"]}) / len(baseline))
        # Query-wise deltas average repeated observations first; repeats aren't independent queries.
        deltas = [statistics.mean(per_query[str(r)][q["id"]][mode][metric] -
                                 per_query[str(r)][q["id"]][reference][metric] for r in range(run["repeats"]))
                  for q in corpus["queries"]]
        item = {"mode": mode, "samples": len(rows), "meanMilliseconds": statistics.mean(r["milliseconds"] for r in rows),
                "p50Milliseconds": percentile([r["milliseconds"] for r in rows], .5),
                "p95Milliseconds": percentile([r["milliseconds"] for r in rows], .95),
                "overlapWithReference": statistics.mean(overlap) if overlap else None,
                "emptyReferenceSamples": len(rows) - len(overlap),
                "ndcgDeltaVsReference": statistics.mean(deltas),
                "wins": sum(d > 1e-12 for d in deltas), "losses": sum(d < -1e-12 for d in deltas),
                "ties": sum(abs(d) <= 1e-12 for d in deltas),
                "qualityPerRepeat": [m[mode] for m in repeated]}
        for measure in repeated[0][mode]:
            if measure not in ("meanSearchMilliseconds", "emptyResults"):
                item[measure] = statistics.mean(m[mode][measure] for m in repeated)
        output.append(item)
    for item in output:
        item["observedParetoFrontier"] = not any(other[metric] >= item[metric] and
            other["p95Milliseconds"] <= item["p95Milliseconds"] and
            (other[metric] > item[metric] or other["p95Milliseconds"] < item["p95Milliseconds"]) for other in output)
    return output, per_query


def profile_summary(path, run):
    if not path.exists():
        return []
    profiles = json.loads(path.read_bytes())
    for key in ["datasetSha256", "schemaSha256", "configurationSha256", "configurations", "k"]:
        require(profiles[key] == run[key], "Profiling metadata mismatch: " + key)

    def walk(value):
        if isinstance(value, dict):
            yield value
            for child in value.values():
                yield from walk(child)
        elif isinstance(value, list):
            for child in value:
                yield from walk(child)

    output = []
    for trace in profiles["traces"]:
        nodes = list(walk(trace["trace"]))
        phases = {}
        for node in nodes:
            if node.get("tag", "").endswith("profiling"):
                phases[node["tag"]] = phases.get(node["tag"], 0.) + node.get("total_time_ms", 0.)
        functions = [n for n in nodes if n.get("name") == "function firstphase"]
        expensive = sorted((n for n in nodes if "self_time_ms" in n), key=lambda n: n["self_time_ms"], reverse=True)[:3]
        output.append({"queryId": trace["queryId"], "mode": trace["mode"], "words": trace["words"],
                       "profiledPhaseMilliseconds": phases,
                       "firstPhaseFunctionEvaluations": sum(n["count"] for n in functions) if functions else None,
                       "observedAlgorithms": sorted({n["algorithm"] for n in nodes if "algorithm" in n}),
                       "largestSelfTimeEntries": [{k: n[k] for k in ["name", "count", "self_time_ms"]} for n in expensive]})
    return output


def report(args):
    data = args.dataset.read_bytes()
    corpus, run = json.loads(data), json.loads(args.run.read_bytes())
    validate(corpus, run, hashlib.sha256(data).hexdigest(), args.reference)
    aggregates, per_query = calculate(corpus, run, args.reference)
    folder = args.run.parent
    profiles = profile_summary(folder / "latency-traces.json", run)
    require(not (folder / "LATENCY.md").exists() and not (folder / "latency-quality.json").exists(), "Report already exists.")
    with (folder / "latency-quality.json").open("x") as output:
        json.dump({"datasetSha256": run["datasetSha256"], "schemaSha256": run["schemaSha256"],
                   "configurationSha256": run["configurationSha256"], "configurations": run["configurations"],
                   "reference": args.reference, "irMeasuresVersion": importlib.metadata.version("ir-measures"),
                   "aggregates": aggregates, "profiles": profiles, "perQuery": per_query}, output, indent=2, allow_nan=False)
    k = run["k"]
    lines = ["# Native retrieval quality and latency", "", f"Dataset: {corpus['metadata']['source']}; {len(corpus['queries'])} queries; {run['repeats']} repeats; k={k}.",
             f"Server: {run.get('serverLabel') or 'unspecified'}. Excluded warmups: {run['warmup']} per configuration.", "",
             f"| Configuration | nDCG@{k} | Precision@3 | Recall@{k} | Mean ms | p50 ms | p95 ms | Delta vs reference | Top-k overlap |",
             "|---|---:|---:|---:|---:|---:|---:|---:|---:|"]
    for row in aggregates:
        overlap = "n/a" if row["overlapWithReference"] is None else f"{row['overlapWithReference']:.4f}"
        p3 = f"{row['P@3']:.4f}" if "P@3" in row else "n/a"
        lines.append(f"| {row['mode']} | {row[f'nDCG@{k}']:.4f} | {p3} | {row[f'R@{k}']:.4f} | {row['meanMilliseconds']:.2f} | {row['p50Milliseconds']:.2f} | {row['p95Milliseconds']:.2f} | {row['ndcgDeltaVsReference']:+.4f} | {overlap} |")
    lines += ["", f"Reference: {args.reference}. Quality is the mean of complete repeat-wise scores, not duplicated query judgments. Paired wins/losses average repetitions per query.",
              "Top-k overlap is the fraction of returned reference IDs recovered, averaged over nonempty reference rankings; short reference rankings use their actual length.",
              "Metric averages retain every prepared query, including those with unavailable positive documents or empty rankings. Published judgments are kept unchanged.",
              "Latency includes request construction, server work, summaries and response processing; excludes embeddings, warmups and profiling. Queries/configurations interleave with seed 1729.",
              "Exact/ANN is explicit per configuration. Compare current arms, not historical timings from another deployment. Serial local p95 does not establish concurrent production capacity.",
              "Observed Pareto membership is descriptive and does not establish a universal default. Interpret the results using the recorded configuration-selection protocol.", ""]
    if profiles:
        lines += ["## Separate instrumented profiles", "", "These profiler timings include instrumentation overhead and are not the latency samples above. Quantiles were fixed by text length, without judgments or previous latency selection.", "",
                  "| Configuration | Query words | Matching ms | First phase ms | First-phase function calls | NN plan |", "|---|---:|---:|---:|---:|---|"]
        for row in profiles:
            phases = row["profiledPhaseMilliseconds"]
            lines.append(f"| {row['mode']} | {row['words']} | {phases.get('match_profiling', 0.):.2f} | {phases.get('first_phase_profiling', 0.):.2f} | {row['firstPhaseFunctionEvaluations'] or 'n/a'} | {', '.join(row['observedAlgorithms']) or 'n/a'} |")
    (folder / "LATENCY.md").write_text("\n".join(lines))
    print("\n".join(lines))


def self_test():
    corpus = {"documents": [{"id": d, "text": d, "vector": [1., 0.]} for d in ["a", "b"]],
              "queries": [{"id": q, "text": q, "keywords": [q], "vector": [1., 0.], "relevance": {"a": 1}} for q in ["good", "empty"]]}
    run = {"datasetSha256": "test", "k": 1, "repeats": 2,
           "configurations": [{"name": m, "approximate": False} for m in ["reference", "small"]],
           "results": [{"queryId": q["id"], "mode": m, "repeat": r, "milliseconds": 1. if m == "small" else 2.,
                        "hits": [{"id": "a", "score": 1.}] if q["id"] == "good" else []}
                       for q in corpus["queries"] for m in ["reference", "small"] for r in range(2)]}
    validate(corpus, run, "test", "reference")
    rows, _ = calculate(corpus, run, "reference")
    require(all(r["nDCG@1"] == .5 and r["emptyReferenceSamples"] == 2 and r["overlapWithReference"] == 1. for r in rows), "Wrong empty/repeat denominators.")
    require(rows[1]["observedParetoFrontier"] and not rows[0]["observedParetoFrontier"], "Wrong observed frontier.")
    require(percentile(list(range(1, 21)), .95) == 19, "Wrong nearest-rank percentile.")
    for kind in ["missing", "duplicate", "unknown", "self-exclusion", "boolean-repeat", "boolean-score"]:
        bad, bad_corpus = deepcopy(run), deepcopy(corpus)
        if kind == "missing":
            bad["results"].pop()
        elif kind == "duplicate":
            bad["results"].append(bad["results"][0])
        elif kind == "unknown":
            bad["results"][0]["hits"][0]["id"] = "unknown"
        elif kind == "boolean-repeat":
            bad["results"][0]["repeat"] = False
        elif kind == "boolean-score":
            bad["results"][0]["hits"][0]["score"] = True
        else:
            bad_corpus["queries"][0].update(id="b", excludeDocumentId="b")
            for row in bad["results"]:
                if row["queryId"] == "good":
                    row["queryId"] = "b"
                    row["hits"] = [{"id": "b", "score": 1.}]
        try:
            validate(bad_corpus, bad, "test", "reference")
        except ValueError:
            continue
        raise AssertionError("Accepted invalid study: " + kind)
    print("Latency checks passed: complete repeat matrices, exclusions, empty denominators, quality and percentiles.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dataset", type=Path)
    parser.add_argument("--run", type=Path)
    parser.add_argument("--reference", default="reference")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
    else:
        require(args.dataset and args.run, "Supply --dataset and --run.")
        report(args)


if __name__ == "__main__":
    main()
