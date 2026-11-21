# /// script
# requires-python = ">=3.11,<3.14"
# dependencies = ["ir-measures==0.4.3"]
# ///
"""Tune native Vespa profiles on dev, freeze the selection, then evaluate on disjoint test queries."""

import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import statistics
import subprocess
import sys


sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("retrieval_quality", Path(__file__).with_name("retrieval-quality.py"))
quality = importlib.util.module_from_spec(spec)
spec.loader.exec_module(quality)
require = quality.require


def digest(data):
    return hashlib.sha256(data).hexdigest()


def write_new(path, value):
    with path.open("x") as file:
        json.dump(value, file, indent=2, allow_nan=False)


def document_digest(corpus):
    return digest(json.dumps(corpus["documents"], sort_keys=True, separators=(",", ":")).encode())


def configuration(mode, vector=100, keyword=100, weight=.5, title=1, rrf_k=60, name=None):
    return {"name": name or f"{mode}_v{vector}_l{keyword}_w{weight:g}_t{title:g}_k{rrf_k}", "mode": mode,
            "vectorCandidates": vector, "keywordCandidates": keyword, "vectorWeight": weight,
            "titleWeight": title, "rrfK": rrf_k}


def baselines():
    return [configuration("lexical", name="lexical"), configuration("semantic", name="semantic"),
            configuration("lexical", title=2)]


def coarse_grid():
    result = baselines()
    for vector, keyword in [(50, 200), (100, 100), (200, 50), (300, 300)]:
        for weight in [.25, .5, .75]:
            for title in [1, 2]:
                result.append(configuration("hybrid_normalized", vector, keyword, weight, title))
                for rrf_k in [10, 60]:
                    result.append(configuration("hybrid_rrf", vector, keyword, weight, title, rrf_k))
    return result


def evaluate(dataset_path, configs, folder, args, deploy=False):
    folder.mkdir()
    config_path = folder / "configurations.json"
    write_new(config_path, configs)
    command = ["dotnet", str(args.runner), "--native", "--dataset", str(dataset_path), "--config", str(config_path),
               "--endpoint", args.endpoint, "--out", str(folder / "run"), "--k", str(args.k),
               "--server-label", args.server_label]
    if deploy:
        command += ["--deploy", "--config-endpoint", args.config_endpoint, "--feed"]
    subprocess.run(command, check=True)
    run_path = folder / "run" / "results.json"
    run = json.loads(run_path.read_bytes())
    corpus_bytes = dataset_path.read_bytes()
    corpus = json.loads(corpus_bytes)
    quality.validate_run(corpus, run, digest(corpus_bytes))
    aggregates, per_query = quality.calculate(corpus, run)
    write_new(folder / "metrics.json", {"aggregates": aggregates, "perQuery": per_query})
    return run, aggregates, per_query


def winners(runs, k):
    entries = []
    for run, aggregates, _ in runs:
        for config in run["configurations"]:
            entries.append((config, aggregates[config["name"]][f"nDCG@{k}"]))
    selected = {}
    for mode in ["lexical", "semantic", "hybrid_normalized", "hybrid_rrf"]:
        eligible = [(c, score) for c, score in entries if c["mode"] == mode]
        require(eligible, "Missing retrieval family: " + mode)
        # Deterministic tie-break favors smaller candidate budgets, then stable parameter order.
        selected[mode] = max(eligible, key=lambda item: (item[1], -(item[0]["vectorCandidates"] + item[0]["keywordCandidates"]),
                                                       -item[0]["titleWeight"], -abs(item[0]["vectorWeight"] - .5), item[0]["name"]))
    return selected


def refine_grid(selected):
    result = baselines()
    for mode in ["hybrid_normalized", "hybrid_rrf"]:
        best, _ = selected[mode]
        weights = sorted({round(min(.95, max(.05, best["vectorWeight"] + delta)), 4) for delta in [-.15, 0, .15]})
        titles = sorted({1, best["titleWeight"], min(4, best["titleWeight"] * 2)})
        targets = {(best["vectorCandidates"], best["keywordCandidates"]),
                   (min(1000, best["vectorCandidates"] * 2), best["keywordCandidates"]),
                   (best["vectorCandidates"], min(1000, best["keywordCandidates"] * 2))}
        for vector, keyword in sorted(targets):
            for weight in weights:
                for title in titles:
                    for rrf_k in ([best["rrfK"], 30, 100] if mode == "hybrid_rrf" else [60]):
                        item = configuration(mode, vector, keyword, weight, title, rrf_k)
                        if item not in result:
                            result.append(item)
    return result


def bootstrap_interval(deltas):
    import numpy as np
    from scipy.stats import bootstrap

    if max(deltas) == min(deltas):
        return [float(deltas[0]), float(deltas[0])]
    interval = bootstrap((np.asarray(deltas),), np.mean, method="percentile", n_resamples=10000,
                         confidence_level=.95, rng=np.random.default_rng(1729)).confidence_interval
    return [float(interval.low), float(interval.high)]


def ensure_test_is_disjoint(selection, test):
    require(test["metadata"]["source"].endswith("/test") and test["metadata"].get("queryLimit") is None,
            "Final evaluation requires the complete published test split.")
    require(not set(selection["devQueryIds"]) & {q["id"] for q in test["queries"]}, "Dev and test query IDs overlap.")
    require(document_digest(test) == selection["documentsSha256"], "Dev/test documents or embeddings differ.")
    require(test["metadata"]["embedding"] == selection["embedding"], "Embedding configuration changed after selection.")


def run_tuning(args):
    require(not args.out.exists(), "Use a new output directory.")
    dev_bytes = args.dev.read_bytes()
    dev = json.loads(dev_bytes)
    quality.validate_corpus(dev)
    require(dev["metadata"]["source"].endswith("/dev") and dev["metadata"].get("queryLimit") is None,
            "Tuning requires the complete dev split; test judgments must not guide the search.")
    require(all("title" in d and "body" in d for d in dev["documents"]), "Prepare native title/body fields first.")
    require(args.config_endpoint and args.deploy, "Initial tuning requires --deploy and a dedicated --config-endpoint.")
    args.out.mkdir(parents=True)
    coarse = coarse_grid()
    print(f"Coarse search: {len(coarse)} configurations on {len(dev['queries'])} dev queries.", flush=True)
    first = evaluate(args.dev, coarse, args.out / "coarse", args, deploy=True)
    best_coarse = winners([first], args.k)
    refined = refine_grid(best_coarse)
    print(f"Refinement: {len(refined)} configurations, dev only.", flush=True)
    second = evaluate(args.dev, refined, args.out / "refined", args)
    chosen = winners([first, second], args.k)
    frozen_configs = [{**config, "name": mode} for mode, (config, _) in chosen.items()]
    best_mode = max(chosen, key=lambda mode: chosen[mode][1])
    selection = {
        "objective": f"dev nDCG@{args.k}", "devDatasetSha256": digest(dev_bytes),
        "documentsSha256": document_digest(dev), "embedding": dev["metadata"]["embedding"],
        "schemaSha256": first[0]["schemaSha256"], "devQueryIds": [q["id"] for q in dev["queries"]],
        "coarseConfigurations": len(coarse), "refinedConfigurations": len(refined),
        "devScores": {mode: score for mode, (_, score) in chosen.items()},
        "configurations": frozen_configs,
        "selected": next(c for c in frozen_configs if c["mode"] == best_mode),
        "testPreviouslyObserved": True,
        "note": "An untuned baseline was observed earlier. Selection uses dev only; this is a frozen test comparison, not a pristine unseen research holdout."
    }
    selection_path = args.out / "selection.json"
    write_new(selection_path, selection)
    print("Selection frozen before reading test judgments: " + str(selection_path), flush=True)

    # No test file is opened until the dev configuration has been selected and persisted.
    test = json.loads(args.test.read_bytes())
    quality.validate_corpus(test)
    ensure_test_is_disjoint(selection, test)
    final = evaluate(args.test, frozen_configs, args.out / "test", args)
    require(final[0]["schemaSha256"] == selection["schemaSha256"], "Schema changed between dev and test.")
    quality.report(argparse.Namespace(dataset=args.test, run=args.out / "test" / "run" / "results.json"))
    metric_name = f"nDCG@{args.k}"
    paired = {}
    for mode in ["lexical", "hybrid_normalized", "hybrid_rrf"]:
        delta = [scores[mode][metric_name] - scores["semantic"][metric_name] for scores in final[2].values()]
        paired[mode] = {"meanDelta": statistics.mean(delta), "pairedBootstrap95Percent": bootstrap_interval(delta),
                        "wins": sum(d > 1e-12 for d in delta), "losses": sum(d < -1e-12 for d in delta)}
    write_new(args.out / "paired-test.json", paired)
    print(json.dumps({"devSelection": selection["selected"], "testMetrics": final[1], "pairedTest": paired}, indent=2), flush=True)


def self_test():
    coarse = coarse_grid()
    require(len({c["name"] for c in coarse}) == len(coarse), "Duplicate grid names.")
    fake_run = {"configurations": coarse}
    fake_metrics = {c["name"]: {"nDCG@10": float(c["vectorWeight"])} for c in coarse}
    selected = winners([(fake_run, fake_metrics, {})], 10)
    require(selected["hybrid_normalized"][0]["vectorWeight"] == .75, "Wrong dev winner.")
    require(refine_grid(selected), "Missing refinement.")
    fake_test = {"metadata": {"source": "BEIR/nfcorpus/test", "embedding": {}}, "documents": [], "queries": [{"id": "shared"}]}
    selection = {"devQueryIds": ["shared"], "documentsSha256": document_digest(fake_test), "embedding": {}}
    try:
        ensure_test_is_disjoint(selection, fake_test)
    except ValueError:
        pass
    else:
        raise AssertionError("Accepted overlapping dev/test queries.")
    print("Tuning checks passed: grid identity, deterministic selection, and dev/test isolation.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--dev", type=Path)
    parser.add_argument("--test", type=Path)
    parser.add_argument("--endpoint")
    parser.add_argument("--deploy", action="store_true")
    parser.add_argument("--config-endpoint")
    parser.add_argument("--out", type=Path)
    parser.add_argument("--k", type=int, default=10)
    parser.add_argument("--runner", type=Path, default=Path("benchmarks/Vespa.NET.RetrievalEvaluation/bin/Release/net10.0/Vespa.NET.RetrievalEvaluation.dll"))
    parser.add_argument("--server-label", default="Record the actual Vespa image and hardware.")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    require(args.dev and args.test and args.endpoint and args.out, "Supply --dev, --test, --endpoint and --out.")
    require(1 <= args.k <= 50, "The tuning grid requires a result cutoff between 1 and 50.")
    run_tuning(args)


if __name__ == "__main__":
    main()
