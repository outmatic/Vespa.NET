# /// script
# requires-python = ">=3.11,<3.14"
# dependencies = ["ir-measures==0.4.3"]
# ///
"""Opt-in BEIR preparation and standard IR evaluation. Never changes a live application."""

import argparse
import csv
import gzip
import hashlib
import importlib.metadata
import io
import json
import math
import os
from pathlib import Path
import re
import statistics
import tempfile
import urllib.request
import urllib.error
import zipfile


SOURCE_TEMPLATE = "https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/{}.zip"
MODES = ["lexical", "semantic", "hybrid_normalized", "hybrid_rrf"]
# Matches Text.isTextCharacter in Vespa; DEL and C1 controls remain allowed.
INVALID_VESPA_TEXT = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\ud800-\udfff\ufdd0-\ufddf"
                               + "".join(chr(plane * 0x10000 + end) for plane in range(17) for end in [0xfffe, 0xffff]) + "]")


def normalize_vespa_text(text):
    return INVALID_VESPA_TEXT.sub(" ", text)


def require(condition, message):
    if not condition:
        raise ValueError(message)


def request_json(url, payload=None):
    data = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=180) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"HTTP {error.code}: {error.read().decode()}") from error


def vector_valid(vector, dimensions=None):
    return (isinstance(vector, list) and len(vector) > 0
            and (dimensions is None or len(vector) == dimensions)
            and all(isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v) for v in vector)
            and any(v != 0 for v in vector))


def validate_source_manifest(manifest, source, documents, queries, judgments, ignore_identical_ids):
    require(isinstance(manifest, dict), "Source manifest must be a JSON object.")
    require(manifest.get("source") == source, "Source manifest refers to another corpus or split.")
    for key, expected in [("documents", documents), ("queries", queries), ("judgments", judgments)]:
        require(type(manifest.get(key)) is int and manifest[key] == expected,
                f"Source manifest {key} count does not match the archive.")
    require(type(manifest.get("ignoreIdenticalIds")) is bool and manifest["ignoreIdenticalIds"] == ignore_identical_ids,
            "Source manifest and preparation use different identical-ID policies.")


def verify_source_member(manifest, key, data):
    expected = manifest["files"][key].get("uncompressedSha256")
    require(isinstance(expected, str) and re.fullmatch(r"[a-f0-9]{64}", expected)
            and hashlib.sha256(data).hexdigest() == expected, "Uncompressed source checksum mismatch: " + key)


def fetch(args):
    require(not args.out.exists(), "Archive output already exists; use a new path.")
    manifest = json.loads(args.source_manifest.read_bytes())
    require(isinstance(manifest, dict) and isinstance(manifest.get("source"), str)
            and re.fullmatch(r"BEIR/[a-z][a-z0-9_-]*/(?:dev|test)", manifest["source"]),
            "Expected a BEIR corpus/split source in the manifest.")
    _, corpus, split = manifest["source"].split("/")
    payloads = {}
    for key in ["corpus", "queries", "qrels"]:
        entry = manifest["files"][key]
        require(isinstance(entry.get("url"), str) and entry["url"].startswith("https://")
                and type(entry.get("bytes")) is int and entry["bytes"] > 0
                and isinstance(entry.get("sha256"), str) and re.fullmatch(r"[a-f0-9]{64}", entry["sha256"]),
                "Source files need HTTPS URLs, byte counts and SHA-256 hashes.")
        with urllib.request.urlopen(entry["url"], timeout=180) as response:
            data = response.read(entry["bytes"] + 1)
        require(len(data) == entry["bytes"] and hashlib.sha256(data).hexdigest() == entry["sha256"],
                "Source file size/checksum mismatch: " + key)
        payloads[key] = gzip.decompress(data) if key != "qrels" else data
        verify_source_member(manifest, key, payloads[key])
    documents = [json.loads(line) for line in payloads["corpus"].splitlines() if line]
    queries = [json.loads(line) for line in payloads["queries"].splitlines() if line]
    judgments = list(csv.DictReader(io.StringIO(payloads["qrels"].decode()), delimiter="\t"))
    validate_source_manifest(manifest, manifest["source"], len(documents),
                             len({r["query-id"] for r in judgments}), len(judgments), manifest.get("ignoreIdenticalIds"))
    require({r["query-id"] for r in judgments}.issubset({q["_id"] for q in queries}), "Missing source queries.")
    require({r["corpus-id"] for r in judgments}.issubset({d["_id"] for d in documents}), "Missing source documents.")
    args.out.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(dir=args.out.parent, suffix=".zip.tmp")
    os.close(descriptor)
    try:
        with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED) as archive:
            for key, name in [("corpus", "corpus.jsonl"), ("queries", "queries.jsonl"), ("qrels", f"qrels/{split}.tsv")]:
                entry = zipfile.ZipInfo(f"{corpus}/{name}", date_time=(1980, 1, 1, 0, 0, 0))
                entry.compress_type = zipfile.ZIP_DEFLATED
                archive.writestr(entry, payloads[key])
        os.link(temporary, args.out)
    finally:
        Path(temporary).unlink(missing_ok=True)
    print(f"Verified and repacked {manifest['source']}: {args.out}")


def validate_corpus(corpus):
    documents, queries = corpus["documents"], corpus["queries"]
    require(documents and queries, "Corpus and judged query set must be nonempty.")
    document_ids = {d["id"] for d in documents}
    unavailable = {d for q in queries for d in q["relevance"] if d not in document_ids}
    require(unavailable == set(corpus.get("metadata", {}).get("unavailableJudgedDocumentIds", [])),
            "Missing judged documents must be explicitly recorded in metadata; all queries stay in the denominator.")
    require(len(document_ids) == len(documents), "Duplicate document IDs.")
    require(len({q["id"] for q in queries}) == len(queries), "Duplicate query IDs.")
    empty_documents = {d["id"] for d in documents if isinstance(d["text"], str) and not d["text"].strip()}
    require(empty_documents == set(corpus.get("metadata", {}).get("emptyDocumentIds", [])),
            "Empty source documents must be explicitly recorded and retained.")
    dimensions = len(documents[0]["vector"])
    ignore_identical_ids = corpus.get("metadata", {}).get("ignoreIdenticalIds")
    require(ignore_identical_ids is None or type(ignore_identical_ids) is bool, "Invalid identical-ID policy.")
    for item in documents + queries:
        require(isinstance(item["id"], str) and item["id"].strip(), "Empty ID.")
        require(isinstance(item["text"], str), "Document/query text must be a string.")
        require(vector_valid(item["vector"], dimensions), "Invalid or inconsistent embedding.")
    for query in queries:
        require(query["text"].strip(), "Empty query text.")
        require(query["keywords"] and all(isinstance(k, str) and k.strip() for k in query["keywords"]), "Missing keywords.")
        require(query["relevance"] and any(v > 0 for v in query["relevance"].values()), "No known relevant documents.")
        require(all(isinstance(d, str) and d.strip() and type(v) is int and 0 <= v <= 3
                    for d, v in query["relevance"].items()), "Invalid qrels.")
        excluded = query.get("excludeDocumentId")
        require(ignore_identical_ids is not False or excluded is None,
                "An identical-ID exclusion contradicts the recorded policy.")
        require(ignore_identical_ids is not True or query["id"] not in document_ids or excluded == query["id"],
                "The recorded identical-ID exclusion policy must apply to every matching ID.")
        require(excluded is None or excluded == query["id"] and excluded in document_ids,
                "Only an existing document with the identical query ID may be excluded.")
        require(excluded is None or query["relevance"].get(excluded, 0) == 0, "Cannot exclude a relevant document.")
    return dimensions


def prepare(args, embedding_factory=None):
    require(args.batch_size > 0, "Batch size must be positive.")
    require(args.context_length > 0, "Context length must be positive.")
    require(args.query_limit is None or args.query_limit > 0, "Query limit must be positive.")
    require(not args.out.exists(), "Dataset output already exists; use a new path.")
    source_manifest_bytes = args.source_manifest.read_bytes() if args.source_manifest else None
    source_manifest = json.loads(source_manifest_bytes) if source_manifest_bytes is not None else None
    require(args.source_manifest is None or isinstance(source_manifest, dict), "Source manifest must be a JSON object.")
    archive_bytes = args.archive.read_bytes()
    with zipfile.ZipFile(io.BytesIO(archive_bytes)) as archive:
        members = {"corpus": archive.read(f"{args.corpus}/corpus.jsonl"),
                   "queries": archive.read(f"{args.corpus}/queries.jsonl"),
                   "qrels": archive.read(f"{args.corpus}/qrels/{args.split}.tsv")}
        if source_manifest is not None:
            for key, data in members.items():
                verify_source_member(source_manifest, key, data)

        def jsonl(name):
            return [json.loads(line) for line in members[name].decode().splitlines() if line]

        documents = jsonl("corpus")
        query_by_id = {q["_id"]: q for q in jsonl("queries")}
        judgments = {}
        reader = csv.DictReader(io.StringIO(members["qrels"].decode()), delimiter="\t")
        for row in reader:
            judgments.setdefault(row["query-id"], {})[row["corpus-id"]] = int(row["score"])
    query_ids = sorted(judgments)
    if args.query_limit is not None:
        query_ids = query_ids[:args.query_limit]
    document_ids = {d["_id"] for d in documents}
    # SciFact claims/papers and FiQA questions/answers have separate ID namespaces.
    ignore_identical_ids = args.ignore_identical_ids
    if ignore_identical_ids is None:
        ignore_identical_ids = args.corpus in {"nfcorpus", "arguana"}
    if source_manifest is not None:
        validate_source_manifest(source_manifest, f"BEIR/{args.corpus}/{args.split}", len(documents),
                                 len(judgments), sum(len(v) for v in judgments.values()), ignore_identical_ids)
    require(not ignore_identical_ids or all(judgments[q].get(q, 0) == 0 for q in query_ids if q in document_ids),
            "Identical-ID exclusion would remove a judged positive document.")
    unavailable = sorted({d for q in query_ids for d in judgments[q] if d not in document_ids})
    if unavailable:
        print(f"Archive has {len(unavailable)} judged IDs absent from the corpus; retaining every query and judgment.", flush=True)
    text_changes = []

    def normalize(kind, identifier, field, text):
        invalid = INVALID_VESPA_TEXT.findall(text)
        if invalid:
            text_changes.append({"kind": kind, "id": identifier, "field": field,
                                 "replacements": {f"U+{ord(c):04X}": invalid.count(c) for c in sorted(set(invalid))}})
        return normalize_vespa_text(text)

    documents = [d | {"title": normalize("document", d["_id"], "title", d.get("title", "")),
                      "text": normalize("document", d["_id"], "body", d["text"])} for d in documents]
    describe = None
    if embedding_factory is None:
        tags = request_json(args.ollama.rstrip("/") + "/api/tags")["models"]
        selected = next((m for m in tags if m["name"] in (args.model, args.model + ":latest")), None)
        require(selected is not None, "Embedding model is not installed on the selected Ollama server.")
        identity = {"model": selected["name"], "digest": selected["digest"], "truncate": args.truncate, "numCtx": args.context_length}

        def generate(batch):
            return request_json(args.ollama.rstrip("/") + "/api/embed", {
                "model": args.model, "input": batch, "truncate": args.truncate, "keep_alive": "10m",
                "options": {"num_ctx": args.context_length}
            })["embeddings"]
    else:
        identity, generate, describe = embedding_factory(args)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    cache_path = args.out.parent / "embedding-cache.jsonl"
    cache = {}
    if cache_path.exists():
        for line in cache_path.read_text().splitlines():
            entry = json.loads(line)
            cache[entry["key"]] = entry["vector"]

    def embed(texts, prefix):
        keys = [hashlib.sha256(json.dumps([identity, prefix + text], sort_keys=True).encode()).hexdigest() for text in texts]
        pending = [(key, prefix + text) for key, text in zip(keys, texts) if key not in cache]
        if getattr(args, "sort_by_length", False):
            pending.sort(key=lambda entry: len(entry[1]))
        with cache_path.open("a") as output:
            for start in range(0, len(pending), args.batch_size):
                batch = pending[start:start + args.batch_size]
                vectors = generate([text for _, text in batch])
                require(len(vectors) == len(batch) and all(vector_valid(v) for v in vectors), "Invalid embedding response.")
                for (key, _), vector in zip(batch, vectors):
                    cache[key] = vector
                    output.write(json.dumps({"key": key, "vector": vector}, separators=(",", ":")) + "\n")
                output.flush()
                if start // args.batch_size % 8 == 0 or start + len(batch) == len(pending):
                    print(f"Embeddings {start + len(batch)}/{len(pending)} ({prefix.strip()})", flush=True)
        return [cache[key] for key in keys]

    # Every mode sees the same full document text. No synthetic vectors or LLM-generated labels.
    texts = [(d.get("title", "") + "\n\n" + d["text"]).strip() for d in documents]
    doc_vectors = embed(texts, args.document_prefix)
    query_texts = [normalize("query", q, "text", query_by_id[q]["text"]) for q in query_ids]
    query_vectors = embed(query_texts, args.query_prefix)
    corpus = {
        "metadata": {
            "source": f"BEIR/{args.corpus}/{args.split}",
            "sourceUrl": args.source_url or SOURCE_TEMPLATE.format(args.corpus),
            "archiveSha256": hashlib.sha256(archive_bytes).hexdigest(),
            "embedding": identity,
            "documentPrefix": args.document_prefix,
            "queryPrefix": args.query_prefix,
            "queryLimit": args.query_limit,
            "judgments": "Published dataset labels, not generated by an LLM; unjudged documents count as nonrelevant.",
            "ignoreIdenticalIds": ignore_identical_ids,
            "emptyDocumentIds": sorted(d["_id"] for d, text in zip(documents, texts) if not text.strip()),
            "textPreprocessing": {"rule": "Replace each code point rejected by Vespa Text.isTextCharacter with U+0020 before indexing and embedding.",
                                  "source": "https://github.com/vespa-engine/vespa/blob/master/vespajlib/src/main/java/com/yahoo/text/Text.java",
                                  "changes": text_changes},
            "unavailableJudgedDocumentIds": unavailable
        },
        "documents": [{"id": d["_id"], "text": text, "title": d.get("title", ""), "body": d["text"], "vector": v}
                      for d, text, v in zip(documents, texts, doc_vectors)],
        "queries": [{"id": q, "text": text, "keywords": re.findall(r"\w+", text), "vector": v, "relevance": judgments[q],
                     "excludeDocumentId": q if ignore_identical_ids and q in document_ids else None}
                    for q, text, v in zip(query_ids, query_texts, query_vectors)]
    }
    if source_manifest is not None:
        corpus["metadata"]["sourceManifest"] = source_manifest
        corpus["metadata"]["sourceManifestSha256"] = hashlib.sha256(source_manifest_bytes).hexdigest()
    if describe is not None:
        corpus["metadata"]["embeddingPreparation"] = describe(corpus)
    dimensions = validate_corpus(corpus)
    # Exclusive creation: a failed preparation cannot replace a previously prepared dataset.
    with args.out.open("x") as output:
        json.dump(corpus, output, separators=(",", ":"), allow_nan=False)
    print(f"Prepared {len(documents)} documents, {len(query_ids)} judged queries, {dimensions} dimensions: {args.out}")


def validate_run(corpus, run, digest):
    validate_corpus(corpus)
    require(run["datasetSha256"] == digest, "Run and judged dataset hashes differ.")
    require(isinstance(run["modes"], list) and len(set(run["modes"])) == len(run["modes"])
            and {"lexical", "semantic"}.issubset(run["modes"]), "Need unique configuration names and lexical/semantic baselines.")
    require(type(run["k"]) is int and run["k"] > 0, "Invalid cutoff.")
    require(run["approximate"] is False, "This comparison expects exact dense retrieval.")
    document_ids = {d["id"] for d in corpus["documents"]}
    query_by_id = {q["id"]: q for q in corpus["queries"]}
    expected = {(q["id"], mode) for q in corpus["queries"] for mode in run["modes"]}
    actual = set()
    for row in run["results"]:
        pair = (row["queryId"], row["mode"])
        require(pair in expected and pair not in actual, "Unknown or duplicate query/mode result.")
        actual.add(pair)
        hits = row["hits"]
        require(len(hits) <= run["k"] and len({h["id"] for h in hits}) == len(hits), "Duplicate or excess hits.")
        require(all(h["id"] in document_ids and math.isfinite(h["score"]) for h in hits), "Unknown document or invalid score.")
        require(all(h["id"] != query_by_id[row["queryId"]].get("excludeDocumentId") for h in hits), "A self-query document was returned despite exclusion.")
        require(math.isfinite(row["milliseconds"]) and row["milliseconds"] >= 0, "Invalid elapsed time.")
    require(actual == expected, "Incomplete run: every query/mode must be present, including empty results.")


def calculate(corpus, run):
    import ir_measures
    from ir_measures import Qrel, ScoredDoc, nDCG, R, RR, P

    k = run["k"]
    measures = [nDCG @ k, R @ k, RR @ k, P @ k]
    if k > 3:
        measures += [nDCG @ 3, P @ 3, P @ 1]
    qrels = [Qrel(q["id"], doc_id, grade) for q in corpus["queries"] for doc_id, grade in q["relevance"].items()]
    aggregates, per_query = {}, {}
    for mode in run["modes"]:
        rows = [r for r in run["results"] if r["mode"] == mode]
        # Preserve actual result ordering even when backend scores tie or differ in meaning across modes.
        scored = [ScoredDoc(row["queryId"], hit["id"], -float(rank)) for row in rows for rank, hit in enumerate(row["hits"], 1)]
        aggregates[mode] = {str(metric): score for metric, score in ir_measures.calc_aggregate(measures, qrels, scored).items()}
        aggregates[mode]["meanSearchMilliseconds"] = statistics.mean(r["milliseconds"] for r in rows)
        aggregates[mode]["emptyResults"] = sum(not r["hits"] for r in rows)
        for result in ir_measures.iter_calc(measures, qrels, scored):
            per_query.setdefault(result.query_id, {}).setdefault(mode, {})[str(result.measure)] = result.value
    return aggregates, per_query


def report(args):
    dataset_bytes = args.dataset.read_bytes()
    corpus = json.loads(dataset_bytes)
    run = json.loads(args.run.read_bytes())
    validate_run(corpus, run, hashlib.sha256(dataset_bytes).hexdigest())
    metrics, per_query = calculate(corpus, run)
    unavailable = corpus["metadata"].get("unavailableJudgedDocumentIds", [])
    unanswerable = sum(not any(grade > 0 and d not in unavailable for d, grade in q["relevance"].items()) for q in corpus["queries"])
    metric_name = f"nDCG@{run['k']}"
    comparisons = {}
    for mode in run["modes"]:
        if mode == "semantic":
            continue
        deltas = [scores[mode][metric_name] - scores["semantic"][metric_name] for scores in per_query.values()]
        comparisons[mode] = {"baseline": "semantic", "meanNdcgDelta": statistics.mean(deltas),
                             "wins": sum(d > 1e-12 for d in deltas), "losses": sum(d < -1e-12 for d in deltas),
                             "ties": sum(abs(d) <= 1e-12 for d in deltas)}
    output = args.run.parent
    report_path = output / "quality.json"
    markdown_path = output / "QUALITY.md"
    require(not report_path.exists() and not markdown_path.exists(), "Quality report already exists.")
    with report_path.open("x") as file:
        json.dump({"irMeasuresVersion": importlib.metadata.version("ir-measures"), "datasetSha256": run["datasetSha256"],
                   "k": run["k"], "backend": run.get("backend", "vectordata"), "schemaSha256": run.get("schemaSha256"),
                   "configurations": run.get("configurations"), "unavailableJudgedDocumentIds": unavailable,
                   "queriesWithoutAvailablePositiveJudgments": unanswerable, "aggregates": metrics, "vsSemantic": comparisons,
                   "perQuery": per_query}, file, indent=2, allow_nan=False)
    k = run["k"]
    lines = ["# Retrieval quality", "", f"Dataset: {corpus['metadata']['source']}. Documents: {len(corpus['documents'])}; judged queries: {len(corpus['queries'])}.",
             "", f"Dataset SHA-256: `{run['datasetSha256']}`. ir_measures: {importlib.metadata.version('ir-measures')}.",
             "", f"Embedding configuration: `{json.dumps(corpus['metadata'].get('embedding', {}), sort_keys=True)}`.", "",
             f"| Mode | nDCG@{k} | Precision@{k} | Recall@{k} | MRR@{k} | Mean search ms | Empty |",
             "|---|---:|---:|---:|---:|---:|---:|"]
    for mode in run["modes"]:
        m = metrics[mode]
        lines.append(f"| {mode} | {m[f'nDCG@{k}']:.4f} | {m[f'P@{k}']:.4f} | {m[f'R@{k}']:.4f} | {m[f'RR@{k}']:.4f} | {m['meanSearchMilliseconds']:.2f} | {m['emptyResults']} |")
    lines += ["", f"Unavailable judged document IDs in the source archive: {len(unavailable)}. Queries without any available positive judgment: {unanswerable}; retained in every metric denominator.",
              f"Identical query/document IDs excluded before retrieval: {sum(q.get('excludeDocumentId') is not None for q in corpus['queries'])}."]
    lines += ["", f"Per-query nDCG@{k} compared with semantic retrieval:", "",
              "| Mode | Mean delta | Wins | Losses | Ties |", "|---|---:|---:|---:|---:|"]
    for mode, comparison in comparisons.items():
        lines.append(f"| {mode} | {comparison['meanNdcgDelta']:+.4f} | {comparison['wins']} | {comparison['losses']} | {comparison['ties']} |")
    if k > 3:
        lines += ["", "Secondary cutoffs for the sample's three-passage context (not additional selection objectives):", "",
                  "| Mode | nDCG@3 | Precision@3 | Precision@1 |", "|---|---:|---:|---:|"]
        for mode in run["modes"]:
            m = metrics[mode]
            lines.append(f"| {mode} | {m['nDCG@3']:.4f} | {m['P@3']:.4f} | {m['P@1']:.4f} |")
    lines += ["", "Metrics use published graded qrels; recall/MRR treat grades >= 1 as relevant. Unjudged documents count as nonrelevant.",
              "Empty rankings count as zero; failed/missing requests abort rather than disappear from the denominator.",
              "Exact dense search isolates retrieval/ranking quality from HNSW recall. Embeddings are precomputed; timings exclude embedding generation and include cold requests.",
              "These are corpus-specific retrieval measurements, not MAF tool-selection or generated-answer quality. No universal default or statistical significance is inferred.",
              "Use a development split to tune budgets/ranking and a held-out test split for the final comparison.", ""]
    with markdown_path.open("x") as file:
        file.write("\n".join(lines))
    print("\n".join(lines))


def self_test():
    from copy import deepcopy
    from tempfile import TemporaryDirectory
    from types import SimpleNamespace
    from unittest.mock import patch

    allowed = "caffè🙂\t\n\r\x7f\x85"
    require(normalize_vespa_text(allowed) == allowed, "Changed permitted Vespa text characters.")
    require(normalize_vespa_text("a\x10\x11b\fc\ufdd0d\ud800e") == "a  b c d e",
            "Rejected code points must become separate spaces without merging words.")
    for plane in range(17):
        require(normalize_vespa_text(chr(plane * 0x10000 + 0xfffe) + chr(plane * 0x10000 + 0xffff)) == "  ",
                "Unicode plane-ending noncharacters must be replaced.")

    source = {"source": "BEIR/scifact/test", "documents": 3, "queries": 2, "judgments": 4, "ignoreIdenticalIds": False}
    validate_source_manifest(source, "BEIR/scifact/test", 3, 2, 4, False)
    invalid_sources = [None, [], source | {"source": "BEIR/fiqa/test"}, source | {"source": "BEIR/scifact/dev"},
                       source | {"documents": 2}, source | {"queries": True}, source | {"judgments": 3},
                       source | {"ignoreIdenticalIds": True}]
    for invalid in invalid_sources:
        try:
            validate_source_manifest(invalid, "BEIR/scifact/test", 3, 2, 4, False)
        except ValueError:
            continue
        raise AssertionError("Accepted a mismatched source manifest.")

    fixture = {
        "corpus": b'{"_id":"a","text":"alpha"}\n{"_id":"b","text":"beta beta"}\n{"_id":"c","text":"c"}\n',
        "queries": b'{"_id":"good","text":"alpha"}\n{"_id":"empty","text":"empty"}\n',
        "qrels": b'query-id\tcorpus-id\tscore\ngood\ta\t2\ngood\tb\t1\nempty\ta\t2\nempty\tb\t1\n'
    }
    downloads = {f"https://example.invalid/{key}": gzip.compress(value, mtime=0) if key != "qrels" else value
                 for key, value in fixture.items()}
    source["files"] = {key: {"url": url, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}
                       for (key, _), (url, data) in zip(fixture.items(), downloads.items())}
    for key, data in fixture.items():
        source["files"][key]["uncompressedSha256"] = hashlib.sha256(data).hexdigest()
        verify_source_member(source, key, data)
    try:
        verify_source_member(source, "corpus", fixture["corpus"].replace(b'"a"', b'"x"'))
    except ValueError:
        pass
    else:
        raise AssertionError("Accepted source text with changed bytes and unchanged record counts.")
    with TemporaryDirectory() as directory:
        manifest = Path(directory) / "source.json"
        manifest.write_text(json.dumps(source))
        args = SimpleNamespace(source_manifest=manifest, out=Path(directory) / "source.zip")
        with patch("urllib.request.urlopen", side_effect=lambda url, **_: io.BytesIO(downloads[url])):
            fetch(args)
            with zipfile.ZipFile(args.out) as archive:
                require(archive.read("scifact/corpus.jsonl") == fixture["corpus"], "Downloaded corpus changed during repacking.")
                require(archive.read("scifact/qrels/test.tsv") == fixture["qrels"], "Downloaded judgments changed during repacking.")
            digest = hashlib.sha256(args.out.read_bytes()).hexdigest()
            try:
                fetch(args)
            except ValueError:
                pass
            else:
                raise AssertionError("Overwrote a completed source archive.")
            require(hashlib.sha256(args.out.read_bytes()).hexdigest() == digest, "Completed archive was modified.")
            generated = []

            def provider(name):
                def generate(texts):
                    generated.extend(texts)
                    return [[1., float(len(text))] for text in texts]
                return {"model": name, "numCtx": 512}, generate, lambda _: {"fixture": True}

            preparation = SimpleNamespace(archive=args.out, source_manifest=manifest, source_url="https://example.invalid",
                                          out=Path(directory) / "first.json", corpus="scifact", split="test",
                                          query_limit=None, ignore_identical_ids=False, batch_size=2, context_length=512,
                                          document_prefix="passage: ", query_prefix="query: ", sort_by_length=True)
            prepare(preparation, lambda _: provider("first"))
            prepared = json.loads(preparation.out.read_text())
            require(len(generated) == 5 and prepared["metadata"]["embeddingPreparation"]["fixture"],
                    "Optional embedding backend did not prepare the complete corpus.")
            require(all(d["vector"] == [1., float(len("passage: " + d["text"]))] for d in prepared["documents"]),
                    "Length-sorted batches changed document/vector alignment.")
            preparation.out = Path(directory) / "cached.json"
            prepare(preparation, lambda _: provider("first"))
            require(len(generated) == 5, "The same model recomputed cached embeddings.")
            preparation.out = Path(directory) / "other-model.json"
            prepare(preparation, lambda _: provider("other"))
            require(len(generated) == 10, "A different model reused another model's cached vectors.")
            source["files"]["corpus"]["sha256"] = "0" * 64
            manifest.write_text(json.dumps(source))
            args.out = Path(directory) / "invalid.zip"
            try:
                fetch(args)
            except ValueError:
                require(not args.out.exists(), "Published an archive after checksum failure.")
            else:
                raise AssertionError("Accepted a source file with a wrong checksum.")

    documents = [{"id": d, "text": d, "vector": [1., 0.]} for d in ["a", "b", "c"]]
    queries = [{"id": q, "text": q, "keywords": [q], "vector": [1., 0.], "relevance": {"a": 2, "b": 1}} for q in ["good", "empty"]]
    corpus = {"documents": documents, "queries": queries}
    run = {"datasetSha256": "test", "k": 2, "modes": MODES, "approximate": False,
           "results": [{"queryId": q["id"], "mode": mode, "milliseconds": 1.,
                        "hits": [{"id": "a", "score": 1.}, {"id": "b", "score": 1.}] if q["id"] == "good" else []}
                       for q in queries for mode in MODES]}
    validate_run(corpus, run, "test")
    metrics, per_query = calculate(corpus, run)
    for mode in MODES:
        for metric in ["nDCG@2", "R@2", "RR@2"]:
            require(metrics[mode][metric] == .5, "Empty queries must count as zero, even with tied backend scores.")
            require(per_query["empty"][mode][metric] == 0., "Missing empty-query metrics.")
    reversed_run = deepcopy(run)
    for row in reversed_run["results"]:
        row["hits"].reverse()
    reversed_metrics, _ = calculate(corpus, reversed_run)
    expected_ndcg = (1 + 2 / math.log2(3)) / (2 + 1 / math.log2(3)) / 2
    require(math.isclose(reversed_metrics["lexical"]["nDCG@2"], expected_ndcg), "Graded judgments and tied-score ordering were not preserved.")
    unavailable_corpus = deepcopy(corpus)
    unavailable_corpus["queries"][1]["relevance"] = {"absent": 1}
    unavailable_corpus["metadata"] = {"unavailableJudgedDocumentIds": ["absent"]}
    validate_run(unavailable_corpus, run, "test")
    missing_metrics, _ = calculate(unavailable_corpus, run)
    require(missing_metrics["semantic"]["nDCG@2"] == .5, "Unavailable judgments must retain their zero-score queries.")
    unavailable_corpus["metadata"] = {}
    try:
        validate_corpus(unavailable_corpus)
    except ValueError:
        pass
    else:
        raise AssertionError("Accepted undeclared missing judgments.")
    empty_source = deepcopy(corpus)
    empty_source["documents"][0]["text"] = ""
    empty_source["metadata"] = {"emptyDocumentIds": ["a"]}
    validate_corpus(empty_source)
    empty_source["metadata"] = {}
    try:
        validate_corpus(empty_source)
    except ValueError:
        pass
    else:
        raise AssertionError("Accepted an undeclared empty source document.")
    separate_ids = deepcopy(corpus)
    separate_ids["queries"][0].update(id="a", relevance={"c": 1})
    separate_ids["metadata"] = {"ignoreIdenticalIds": False}
    validate_corpus(separate_ids)
    separate_ids["queries"][0]["excludeDocumentId"] = "a"
    try:
        validate_corpus(separate_ids)
    except ValueError:
        pass
    else:
        raise AssertionError("Excluded a document despite the explicit separate-ID policy.")
    separate_ids["metadata"]["ignoreIdenticalIds"] = True
    validate_corpus(separate_ids)
    separate_ids["queries"][0].pop("excludeDocumentId")
    try:
        validate_corpus(separate_ids)
    except ValueError:
        pass
    else:
        raise AssertionError("Ignored the explicit identical-ID exclusion policy.")
    for change in ["missing", "duplicate", "unknown", "hash", "invalid-vector"]:
        bad, bad_corpus = deepcopy(run), deepcopy(corpus)
        if change == "missing":
            bad["results"].pop()
        elif change == "duplicate":
            bad["results"].append(bad["results"][0])
        elif change == "unknown":
            bad["results"][0]["hits"][0]["id"] = "unknown"
        elif change == "hash":
            bad["datasetSha256"] = "stale"
        else:
            bad_corpus["queries"][0]["vector"] = [float("nan"), 0.]
        try:
            validate_run(bad_corpus, bad, "test")
        except ValueError:
            continue
        raise AssertionError("Accepted invalid evaluation: " + change)
    print("Quality evaluation checks passed: empty rankings, tied scores, complete coverage, corpus identity, and invalid inputs.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    download = commands.add_parser("fetch", help="Download checksum-pinned source files and repack a BEIR archive.")
    download.add_argument("--source-manifest", type=Path, required=True)
    download.add_argument("--out", type=Path, required=True)
    prep = commands.add_parser("prepare", help="Embed a downloaded BEIR archive with an existing Ollama model.")
    prep.add_argument("--corpus", choices=["nfcorpus", "arguana", "scifact", "fiqa"], default="nfcorpus")
    prep.add_argument("--archive", type=Path, required=True)
    prep.add_argument("--source-url", help="Source URL recorded in metadata; defaults to the official BEIR archive URL.")
    prep.add_argument("--source-manifest", type=Path,
                      help="Validate and record JSON provenance: source, document/query/judgment counts and ignoreIdenticalIds.")
    prep.add_argument("--out", type=Path, required=True)
    prep.add_argument("--split", choices=["dev", "test"], default="test")
    prep.add_argument("--query-limit", type=int)
    prep.add_argument("--ignore-identical-ids", action=argparse.BooleanOptionalAction, default=None,
                      help="Exclude equal query/document IDs before retrieval; defaults on for NFCorpus/ArguAna, off for SciFact/FiQA.")
    prep.add_argument("--ollama", default="http://localhost:11434")
    prep.add_argument("--model", default="nomic-embed-text")
    prep.add_argument("--document-prefix", default="search_document: ")
    prep.add_argument("--query-prefix", default="search_query: ")
    prep.add_argument("--batch-size", type=int, default=8)
    prep.add_argument("--context-length", type=int, default=8192)
    prep.add_argument("--truncate", action="store_true", help="Explicitly allow Ollama to truncate overlength embedding inputs; recorded in metadata.")
    rep = commands.add_parser("report", help="Score a complete run with ir_measures; never contacts a service.")
    rep.add_argument("--dataset", type=Path, required=True)
    rep.add_argument("--run", type=Path, required=True)
    commands.add_parser("self-test", help="Check evaluation invariants offline, without Vespa or a model.")
    args = parser.parse_args()
    {"fetch": fetch, "prepare": prepare, "report": report, "self-test": lambda _: self_test()}[args.command](args)


if __name__ == "__main__":
    main()
