# /// script
# requires-python = ">=3.11,<3.14"
# dependencies = ["ir-measures==0.4.3"]
# ///
"""Validate and report closed-loop load experiments from local files."""

import argparse
from collections import Counter, defaultdict
from copy import deepcopy
import hashlib
import importlib.metadata
import importlib.util
import itertools
import json
import math
from pathlib import Path
import statistics
import sys

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("ann_quality", Path(__file__).with_name("ann-quality.py"))
ann = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ann)
require = ann.require


def finite(value):
    return type(value) in (float, int) and math.isfinite(value)


def case_key(row):
    require(type(row["scopePercent"]) is int and type(row["concurrency"]) is int, "Invalid case coordinates.")
    return row["mode"], row["scopePercent"], row["concurrency"]


def validate(corpus, run, digest):
    ann.quality.validate_corpus(corpus)
    require(run["datasetSha256"] == digest, "Dataset/run hash mismatch.")
    require(all(type(run[key]) is int and run[key] >= minimum for key, minimum in
                [("k", 1), ("repeats", 1), ("warmup", 0)]), "Invalid cutoff, repeats or warmups.")
    modes = [c["name"] for c in run["configurations"]]
    scopes, levels = run["scopes"], run["concurrencies"]
    require(modes and all(isinstance(m, str) and m.strip() for m in modes) and len(set(modes)) == len(modes), "Invalid profile names.")
    require(scopes and all(type(s) is int and 1 <= s <= 100 for s in scopes) and len(set(scopes)) == len(scopes), "Invalid scopes.")
    require(1 in levels and all(type(n) is int and n >= 1 for n in levels) and len(set(levels)) == len(levels), "Invalid concurrency or missing serial control.")
    expected = set(itertools.product(modes, scopes, levels))
    stats = {}
    queries = {q["id"]: q for q in corpus["queries"]}
    buckets = {d["id"]: ann.bucket(d["id"]) for d in corpus["documents"]}
    samples = len(queries) * run["repeats"]
    for case in run["cases"]:
        key = case_key(case)
        require(key in expected and key not in stats, "Unexpected/duplicate case.")
        require(all(type(case[f]) is int for f in ("attempts", "successful", "warmupFailures"))
                and case["attempts"] == samples and 0 <= case["successful"] <= samples
                and 0 <= case["warmupFailures"] <= run["warmup"], "Invalid case counts.")
        require(finite(case["elapsedMilliseconds"]) and case["elapsedMilliseconds"] > 0, "Invalid case duration.")
        stats[key] = case
    require(stats.keys() == expected, "Incomplete case matrix.")
    groups = defaultdict(dict)
    for row in run["results"]:
        key = case_key(row)
        qid, repeat = row["queryId"], row["repeat"]
        require(key in stats and qid in queries and type(repeat) is int and 0 <= repeat < run["repeats"]
                and (qid, repeat) not in groups[key], "Unexpected/duplicate attempt.")
        require(finite(row["milliseconds"]) and row["milliseconds"] >= 0 and type(row["success"]) is bool, "Invalid latency/status.")
        hits = row["hits"]
        require(isinstance(hits, list) and len(hits) <= run["k"] and len({h["id"] for h in hits}) == len(hits), "Invalid hits.")
        require(all(h["id"] in buckets and buckets[h["id"]] < key[1] and h["id"] != queries[qid].get("excludeDocumentId")
                    and finite(h["score"]) for h in hits), "Invalid result identity, scope, exclusion or score.")
        if row["success"]:
            require(row["errorCode"] is None and row["error"] is None, "Successful attempt has error.")
        else:
            require(not hits and all(isinstance(row[k], str) and row[k].strip() for k in ("error", "errorCode")), "Invalid failed attempt.")
        groups[key][qid, repeat] = row
    for key, case in stats.items():
        rows = groups[key]
        require(len(rows) == samples and sum(r["success"] for r in rows.values()) == case["successful"], "Missing attempts or inconsistent success count.")
    return stats, groups


def algorithms(tree):
    if isinstance(tree, dict):
        if isinstance(tree.get("algorithm"), str):
            yield tree["algorithm"]
        for child in tree.values():
            yield from algorithms(child)
    elif isinstance(tree, list):
        for child in tree:
            yield from algorithms(child)


def plans(corpus, run, traces):
    for key in ("datasetSha256", "schemaSha256", "configurationSha256", "configurations", "scopes", "k", "serverLabel"):
        require(traces[key] == run[key], "Trace metadata mismatch: " + key)
    by_length = sorted(corpus["queries"], key=lambda q: (len(q["text"].split()), q["id"]))
    query_ids = {by_length[len(by_length) // 2]["id"], by_length[len(by_length) * 9 // 10]["id"]}
    expected = set(itertools.product([c["name"] for c in run["configurations"]], run["scopes"], query_ids))
    observed = {}
    for entry in traces["traces"]:
        key = entry["mode"], entry["scopePercent"], entry["queryId"]
        require(key in expected and key not in observed and isinstance(entry["trace"], dict) and entry["trace"], "Invalid diagnostic trace.")
        observed[key] = sorted(set(algorithms(entry["trace"])))
    require(observed.keys() == expected, "Incomplete diagnostic traces.")
    return observed


def calculate(corpus, run, stats, groups):
    from ir_measures import Qrel, ScoredDoc, calc_aggregate, nDCG

    metric = nDCG @ run["k"]
    scoped = {}
    for scope in run["scopes"]:
        docs = {d["id"] for d in corpus["documents"] if ann.bucket(d["id"]) < scope}
        judged, qrels = set(), []
        for q in corpus["queries"]:
            eligible = {d: grade for d, grade in q["relevance"].items() if d in docs and d != q.get("excludeDocumentId")}
            if any(eligible.values()):
                judged.add(q["id"])
                qrels.extend(Qrel(q["id"], d, grade) for d, grade in eligible.items())
        scoped[scope] = len(docs), judged, qrels
    output = {}
    for key in sorted(stats):
        mode, scope, concurrency = key
        case, rows = stats[key], groups[key]
        doc_count, judged, qrels = scoped[scope]
        serial = groups[mode, scope, 1]
        valid_times, overlaps, repeat_quality = [], [], []
        errors = Counter()
        excluded_empty = excluded_failure = 0
        for index, row in rows.items():
            if row["success"]:
                valid_times.append(row["milliseconds"])
            else:
                errors[row["errorCode"]] += 1
            reference = serial[index]
            if not reference["success"]:
                excluded_failure += 1
            elif not reference["hits"]:
                excluded_empty += 1
            else:
                truth = {h["id"] for h in reference["hits"]}
                overlaps.append(len(truth.intersection(h["id"] for h in row["hits"])) / len(truth))
        if judged:
            for repeat in range(run["repeats"]):
                scored = [ScoredDoc(qid, hit["id"], -float(rank)) for qid in judged
                          for rank, hit in enumerate(rows[qid, repeat]["hits"], 1)]
                repeat_quality.append(calc_aggregate([metric], qrels, scored)[metric])
        conditional = statistics.mean(repeat_quality) if judged else None
        seconds = case["elapsedMilliseconds"] / 1000
        output[key] = {**case, "eligibleDocumentsBeforeSelfExclusion": doc_count, "positiveQueries": len(judged),
                       "totalQueries": len(corpus["queries"]), "failuresByCode": dict(errors),
                       "attemptsPerSecond": len(rows) / seconds, "validPerSecond": len(valid_times) / seconds,
                       "p95Milliseconds": ann.percentile([r["milliseconds"] for r in rows.values()], .95),
                       "validP95Milliseconds": ann.percentile(valid_times, .95) if valid_times else None,
                       "conditionalNdcg": conditional, "conditionalNdcgByRepeat": repeat_quality,
                       "allQueryNdcg": (conditional or 0.) * len(judged) / len(corpus["queries"]),
                       "overlapWithSerial": statistics.mean(overlaps) if overlaps else None,
                       "overlapSamples": len(overlaps), "emptySerialSamples": excluded_empty, "failedSerialSamples": excluded_failure}
    for (mode, scope, concurrency), item in output.items():
        baseline = output[mode, scope, 1]
        item["conditionalNdcgDeltaVsSerial"] = item["conditionalNdcg"] - baseline["conditionalNdcg"] if item["positiveQueries"] else None
        item["allQueryNdcgDeltaVsSerial"] = item["allQueryNdcg"] - baseline["allQueryNdcg"]
    return list(output.values())


def display(value, digits=4):
    return "n/a" if value is None else f"{value:.{digits}f}"


def report(args):
    data = args.dataset.read_bytes()
    corpus, run = json.loads(data), json.loads(args.run.read_bytes())
    folder = args.run.parent
    stats, groups = validate(corpus, run, hashlib.sha256(data).hexdigest())
    observed = plans(corpus, run, json.loads((folder / "load-traces.json").read_bytes()))
    rows = calculate(corpus, run, stats, groups)
    summary = {k: run[k] for k in ("datasetSha256", "schemaSha256", "configurationSha256", "corpusSha256",
                                    "configurations", "runtime", "serverLabel", "k", "warmup", "repeats", "completedAtUtc")}
    summary.update(irMeasuresVersion=importlib.metadata.version("ir-measures"), aggregates=rows, diagnosticPlans=[
        {"mode": m, "scopePercent": s, "queryId": q, "algorithms": a} for (m, s, q), a in sorted(observed.items())])
    lines = ["# Filtered retrieval under load", "", f"{corpus['metadata']['source']} · {len(corpus['queries'])} queries · "
             f"{run['repeats']} repeats · k={run['k']} · {run['warmup']} excluded warmups per case.",
             "", f"Server: {run['serverLabel']}", "",
             "| Profile | Scope % | Docs | Concurrency | Attempts | Failures | Attempts/s | Valid/s | p95 ms | Judged queries | Conditional nDCG | All-query nDCG | Conditional Δ vs serial | Overlap |",
             "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"]
    for r in rows:
        values = [r["mode"], r["scopePercent"], r["eligibleDocumentsBeforeSelfExclusion"], r["concurrency"], r["attempts"],
                  r["attempts"] - r["successful"], display(r["attemptsPerSecond"], 2), display(r["validPerSecond"], 2),
                  display(r["p95Milliseconds"], 2), r["positiveQueries"], display(r["conditionalNdcg"]), display(r["allQueryNdcg"]),
                  display(r["conditionalNdcgDeltaVsSerial"]), display(r["overlapWithSerial"])]
        lines.append("| " + " | ".join(map(str, values)) + " |")
    lines += ["", "Document counts describe hash-bucket scope before per-query self-exclusion. Conditional nDCG uses queries with an eligible positive judgment. "
              "All-query nDCG assigns zero to every other query. Failed attempts receive zero in both metrics. Each query receives equal weight across repeats.",
              "", "Overlap measures recovery of same-profile, same-scope serial result IDs for the identical query and repeat. Empty or failed serial references are excluded. "
              "The JSON records overlap denominators, error counts, warmup failures and valid-only p95.",
              "", "Throughput divides attempts or valid responses by elapsed case time. The p95 includes all attempts. "
              "Closed-loop concurrency measures achieved throughput at each tested level. Maximum capacity and fixed-arrival behavior require different experiments. "
              "Latency excludes external queueing, embedding generation, warmups and profiling.",
              "", "## Separately profiled Vespa plans", "", "| Profile | Scope % | Query | Observed algorithms |", "|---|---:|---|---|"]
    for (mode, scope, query), alg in sorted(observed.items()):
        lines.append(f"| {mode} | {scope} | {query} | {', '.join(alg) or 'unreported'} |")
    lines += ["", "These observations apply to the diagnostic requests. Timed requests ran separately.", ""]
    require(not any((folder / name).exists() for name in ("load-quality.json", "LOAD.md")), "Report already exists.")
    with (folder / "load-quality.json").open("x") as file:
        json.dump(summary, file, indent=2, allow_nan=False)
    with (folder / "LOAD.md").open("x") as file:
        file.write("\n".join(lines))
    print("\n".join(lines))


def self_test():
    # The second positive is outside the chosen scope.
    inside, outside = sorted(["a", "b"], key=ann.bucket)
    scope = ann.bucket(inside) + 1
    require(ann.bucket(outside) >= scope, "Test requires distinct buckets.")
    docs = [{"id": d, "text": d, "vector": [1.]} for d in (inside, outside)]
    queries = [{"id": "q", "text": "a", "vector": [1.], "keywords": ["a"], "relevance": {inside: 1}},
               {"id": inside, "text": "a b", "vector": [1.], "keywords": ["a"], "relevance": {outside: 1}, "excludeDocumentId": inside}]
    corpus = {"documents": docs, "queries": queries}
    run = {"datasetSha256": "digest", "configurations": [{"name": "m"}], "scopes": [scope], "concurrencies": [1, 2],
           "k": 1, "repeats": 2, "warmup": 1, "cases": [], "results": []}
    for concurrency in run["concurrencies"]:
        coord = {"mode": "m", "scopePercent": scope, "concurrency": concurrency}
        run["cases"].append({**coord, "attempts": 4, "successful": 4 if concurrency == 1 else 3,
                             "warmupFailures": 0, "elapsedMilliseconds": 1000})
        for query, repeat in itertools.product(queries, range(2)):
            ok = (concurrency, query["id"], repeat) != (2, "q", 1)
            run["results"].append({**coord, "queryId": query["id"], "repeat": repeat, "success": ok, "errorCode": None if ok else "timeout",
                                   "error": None if ok else "timed out", "milliseconds": 1 if ok else 100,
                                   "hits": [{"id": inside, "score": 1.}] if ok and query["id"] == "q" else []})
    rows = calculate(corpus, run, *validate(corpus, run, "digest"))
    row = rows[1]
    require(row["conditionalNdcg"] == .5 and row["allQueryNdcg"] == .25 and row["conditionalNdcgDeltaVsSerial"] == -.5 and
            row["overlapWithSerial"] == .5 and row["emptySerialSamples"] == 2 and row["positiveQueries"] == 1 and
            row["attemptsPerSecond"] == 4 and row["validPerSecond"] == 3 and row["p95Milliseconds"] == 100 and row["validP95Milliseconds"] == 1,
            "Incorrect scoped quality, overlap, failure or throughput denominators.")
    require(ann.percentile(list(range(1, 21)), .95) == 19, "Incorrect nearest-rank percentile.")
    for problem in ("missing", "duplicate", "unknown", "scope", "self", "failed-hits", "success-type", "repeat-type", "case-count", "duplicate-case"):
        bad = deepcopy(run)
        rows = bad["results"]
        if problem == "missing":
            rows.pop()
        elif problem == "duplicate":
            rows.append(rows[0])
        elif problem in ("unknown", "scope"):
            rows[0]["hits"][0]["id"] = "unknown" if problem == "unknown" else outside
        elif problem == "self":
            rows[2]["hits"] = rows[0]["hits"]
        elif problem == "failed-hits":
            rows[5]["hits"] = rows[0]["hits"]
        elif problem == "success-type":
            rows[0]["success"] = 1
        elif problem == "repeat-type":
            rows[0]["repeat"] = False
        elif problem == "case-count":
            bad["cases"][0]["successful"] -= 1
        else:
            bad["cases"].append(bad["cases"][0])
        try:
            validate(corpus, bad, "digest")
        except ValueError:
            continue
        raise AssertionError("Accepted invalid run: " + problem)
    print("Load validation and scoring self-test passed.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dataset", type=Path)
    parser.add_argument("--run", type=Path)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
    else:
        require(args.dataset and args.run, "Supply --dataset and --run.")
        report(args)
