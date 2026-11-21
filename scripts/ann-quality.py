# /// script
# requires-python = ">=3.11,<3.14"
# dependencies = ["ir-measures==0.4.3"]
# ///
"""Validate and report native Vespa HNSW/filter quality and warmed serial latency."""

import argparse
from collections import defaultdict
from copy import deepcopy
import hashlib
import importlib.util
import importlib.metadata
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


def bucket(identifier):
    return int.from_bytes(hashlib.sha256(identifier.encode()).digest()[:4], "big") % 100


def validate(corpus, run, digest):
    quality.validate_corpus(corpus)
    require(run["datasetSha256"] == digest, "ANN run and corpus hashes differ.")
    require(type(run["k"]) is int and run["k"] > 0 and type(run["repeats"]) is int and run["repeats"] > 0, "Invalid cutoff/repeats.")
    modes = [c["name"] for c in run["configurations"]]
    require(len(modes) == 2 and len(set(modes)) == 2, "Need distinct semantic and hybrid configurations.")
    scopes, explorations = run["scopes"], run["explorations"]
    require(scopes and len(set(scopes)) == len(scopes) and all(type(s) is int and 1 <= s <= 100 for s in scopes), "Invalid scopes.")
    require(explorations and len(set(explorations)) == len(explorations) and all(type(e) is int and e >= 0 for e in explorations), "Invalid explorations.")
    docs = {d["id"]: d for d in corpus["documents"]}
    queries = {q["id"]: q for q in corpus["queries"]}
    expected = {(mode, scope, q) for mode in modes for scope in scopes for q in queries}
    baselines = {}

    def hits_valid(row):
        hits = row["hits"]
        require(len(hits) <= run["k"] and len({h["id"] for h in hits}) == len(hits), "Duplicate/excess hits.")
        require(all(h["id"] in docs and bucket(h["id"]) < row["scopePercent"]
                    and h["id"] != queries[row["queryId"]].get("excludeDocumentId") and math.isfinite(h["score"]) for h in hits),
                "ANN result violates document identity or filter scope.")

    for row in run["baselines"]:
        key = (row["mode"], row["scopePercent"], row["queryId"])
        require(key in expected and key not in baselines, "Unknown/duplicate exact baseline.")
        hits_valid(row)
        baselines[key] = [h["id"] for h in row["hits"]]
    require(set(baselines) == expected, "Missing exact baseline query/filter pairs.")
    scenarios = [(False, None)] + [(True, e) for e in explorations]
    measured_expected = {(mode, scope, approximate, explore, repeat, q) for mode in modes for scope in scopes
                         for approximate, explore in scenarios for repeat in range(run["repeats"]) for q in queries}
    seen = set()
    for row in run["results"]:
        key = (row["mode"], row["scopePercent"], row["approximate"], row["explore"], row["repeat"], row["queryId"])
        require(key in measured_expected and key not in seen, "Unknown/duplicate ANN measurement.")
        hits_valid(row)
        require(math.isfinite(row["milliseconds"]) and row["milliseconds"] >= 0, "Invalid latency.")
        seen.add(key)
    require(seen == measured_expected, "Incomplete ANN matrix; failed queries cannot be omitted.")
    return baselines


def percentile(values, quantile):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(quantile * len(ordered)) - 1)]


def calculate(corpus, run, baselines):
    from ir_measures import calc_aggregate, Qrel, ScoredDoc, nDCG, P

    grouped = defaultdict(list)
    for row in run["results"]:
        grouped[(row["mode"], row["scopePercent"], row["approximate"], row["explore"])].append(row)
    output = []
    for (mode, scope, approximate, explore), rows in sorted(grouped.items(), key=lambda pair: str(pair[0])):
        eligible = {d["id"] for d in corpus["documents"] if bucket(d["id"]) < scope}
        qrels, quality_query_ids = [], set()
        for query in corpus["queries"]:
            judgments = {d: grade for d, grade in query["relevance"].items() if d in eligible and d != query.get("excludeDocumentId")}
            if not any(grade > 0 for grade in judgments.values()):
                continue
            quality_query_ids.add(query["id"])
            qrels.extend(Qrel(query["id"], d, grade) for d, grade in judgments.items())
        scored = [ScoredDoc(row["queryId"], hit["id"], -float(rank)) for row in rows
                  if row["repeat"] == 0 and row["queryId"] in quality_query_ids
                  for rank, hit in enumerate(row["hits"], 1)]
        quality_metrics = calc_aggregate([nDCG @ run["k"], P @ run["k"]], qrels, scored) if qrels else {}
        overlap = []
        for row in rows:
            truth = baselines[(mode, scope, row["queryId"])]
            if truth:
                overlap.append(len(set(truth) & {h["id"] for h in row["hits"]}) / len(truth))
        output.append({"mode": mode, "scopePercent": scope, "eligibleDocuments": len(eligible), "approximateRequested": approximate,
                       "explore": explore, "samples": len(rows), "overlapAtK": statistics.mean(overlap) if overlap else None,
                       "emptyExactBaselineSamples": len(rows) - len(overlap),
                       "p50Milliseconds": percentile([r["milliseconds"] for r in rows], .5),
                       "p95Milliseconds": percentile([r["milliseconds"] for r in rows], .95),
                       "judgedQualityQueries": len(quality_query_ids), "queriesWithoutKnownRelevantInScope": len(corpus["queries"]) - len(quality_query_ids),
                       **{str(metric): value for metric, value in quality_metrics.items()}})
    return output


def report(args):
    data = args.dataset.read_bytes()
    corpus, run = json.loads(data), json.loads(args.run.read_bytes())
    baselines = validate(corpus, run, hashlib.sha256(data).hexdigest())
    aggregates = calculate(corpus, run, baselines)
    plans = json.loads(args.plans.read_bytes()) if args.plans else run
    for key in ["datasetSha256", "schemaSha256", "corpusSha256", "k", "scopes", "explorations", "configurations"]:
        require(plans[key] == run[key], "Supplemental plan metadata differs: " + key)

    def algorithms(value):
        if isinstance(value, dict):
            found = [value["algorithm"]] if isinstance(value.get("algorithm"), str) else []
            return found + [a for v in value.values() for a in algorithms(v)]
        if isinstance(value, list):
            return [a for v in value for a in algorithms(v)]
        return []

    traces = {(t["mode"], t["scopePercent"], t["explore"]): sorted(set(algorithms(t["trace"]))) for t in plans["traces"]}
    expected_traces = {(c["name"], scope, e) for c in run["configurations"] for scope in run["scopes"] for e in run["explorations"]}
    require(set(traces) == expected_traces and len(plans["traces"]) == len(expected_traces), "Missing or duplicate diagnostic plans.")
    for row in aggregates:
        row["observedPlanAlgorithms"] = traces.get((row["mode"], row["scopePercent"], row["explore"]), [])
    output = args.out or args.run.parent
    output.mkdir(parents=True, exist_ok=True)
    require(not (output / "ann-quality.json").exists() and not (output / "ANN-QUALITY.md").exists(), "ANN report already exists.")
    with (output / "ann-quality.json").open("x") as file:
        json.dump({"datasetSha256": run["datasetSha256"], "schemaSha256": run["schemaSha256"],
                   "irMeasuresVersion": importlib.metadata.version("ir-measures"), "serverLabel": run.get("serverLabel"),
                   "repeats": run["repeats"], "warmup": run["warmup"], "configurations": run["configurations"],
                   "aggregates": aggregates}, file, indent=2, allow_nan=False)
    k = run["k"]
    lines = ["# Native HNSW and filtered retrieval", "", f"Dataset: {corpus['metadata']['source']}; queries: {len(corpus['queries'])}; k={k}.", "",
             f"Server: {run.get('serverLabel') or 'unspecified'}. Repeats: {run['repeats']}; excluded warmups per scenario: {run['warmup']}.", "",
             "| Mode | Scope % | Eligible docs | Requested search / extra hits | Overlap with exact | p50 ms | p95 ms | nDCG | Judged queries |",
             "|---|---:|---:|---|---:|---:|---:|---:|---:|"]
    for row in aggregates:
        label = f"ANN / {row['explore']}" if row["approximateRequested"] else "Exact"
        overlap = "n/a" if row["overlapAtK"] is None else f"{row['overlapAtK']:.4f}"
        ndcg = f"{row[f'nDCG@{k}']:.4f}" if f"nDCG@{k}" in row else "n/a"
        lines.append(f"| {row['mode']} | {row['scopePercent']} | {row['eligibleDocuments']} | {label} | {overlap} | {row['p50Milliseconds']:.2f} | {row['p95Milliseconds']:.2f} | {ndcg} | {row['judgedQualityQueries']} |")
    lines += ["", "Observed algorithms in separate diagnostic requests (one query per scenario, not every timed request):", "",
              "| Mode | Scope % | Extra hits | Observed plan |", "|---|---:|---:|---|"]
    for (mode, scope, explore), observed in sorted(traces.items()):
        lines.append(f"| {mode} | {scope} | {explore} | {', '.join(observed) or 'not exposed at this trace level'} |")
    lines += ["", "Overlap uses every nonempty exact baseline across every repeat. Relevance metrics use repeat zero and only queries with known positive judgments inside the same filter scope; their separate denominators are recorded.",
              "Unjudged documents count as nonrelevant. All request/filter pairs must succeed with complete coverage.",
              "ANN is requested, not forced: Vespa can fall back to exact search for selective filters. Separate profiled traces are saved in the run or supplemental plan file and excluded from timing samples.",
              "Latencies are warmed serial client observations, excluding embedding generation. This is not a throughput benchmark or a production p95 guarantee. Strict ID overlap can penalize equal-score boundary ties.", ""]
    with (output / "ANN-QUALITY.md").open("x") as file:
        file.write("\n".join(lines))
    print("\n".join(lines))


def self_test():
    corpus = {"documents": [{"id": d, "text": d, "vector": [1., 0.]} for d in ["a", "b"]],
              "queries": [{"id": "q", "text": "q", "keywords": ["q"], "vector": [1., 0.], "relevance": {"a": 1}}]}
    modes = ["semantic", "hybrid_normalized"]
    run = {"datasetSha256": "test", "k": 1, "scopes": [100], "explorations": [0], "repeats": 1,
           "configurations": [{"name": m} for m in modes],
           "baselines": [{"mode": m, "scopePercent": 100, "queryId": "q", "hits": [{"id": "a", "score": 1.}]} for m in modes],
           "results": [{"mode": m, "scopePercent": 100, "queryId": "q", "approximate": approximate, "explore": explore,
                        "repeat": 0, "milliseconds": 1., "hits": [{"id": "a", "score": 1.}]}
                       for m in modes for approximate, explore in [(False, None), (True, 0)]]}
    truth = validate(corpus, run, "test")
    require(all(row["overlapAtK"] == 1. and row["P@1"] == 1. for row in calculate(corpus, run, truth)), "Wrong exact agreement/relevance.")
    for kind in ["missing", "duplicate", "unknown"]:
        bad = deepcopy(run)
        if kind == "missing":
            bad["results"].pop()
        elif kind == "duplicate":
            bad["results"].append(bad["results"][0])
        else:
            bad["results"][0]["hits"][0]["id"] = "unknown"
        try:
            validate(corpus, bad, "test")
        except ValueError:
            continue
        raise AssertionError("Accepted invalid ANN matrix: " + kind)
    print("ANN evaluation checks passed: exact agreement, metric denominators, complete matrices and result identity.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--dataset", type=Path)
    parser.add_argument("--run", type=Path)
    parser.add_argument("--plans", type=Path, help="Optional supplemental --trace-only output with matching metadata.")
    parser.add_argument("--out", type=Path, help="Fresh report directory; defaults to the run directory.")
    args = parser.parse_args()
    if args.self_test:
        self_test()
    else:
        require(args.dataset and args.run, "Supply --dataset and --run.")
        report(args)


if __name__ == "__main__":
    main()
