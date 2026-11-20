# Release scope: native retrieval examples

This update adds a working Agent Framework retrieval example and tools for measuring its results. The example uses query and ranking options already available in the native client.

## Included in the source update

| Area | Changes |
|---|---|
| Agent Framework sample | Text, vector and hybrid documentation search; settings for language, fields, summaries and ranking |
| Sample schema | Separate title and body indexes, a language field, an exact identifier field and BM25/vector rank profiles |
| Presets | NFCorpus and ArguAna configurations, plus a frozen five-profile comparison on SciFact and FiQA |
| Source manifests | Pinned BEIR downloads with compressed and uncompressed checksums |
| Embedding comparison | Optional local E5 preparer and pinned model manifest; the sample keeps its existing embedding configuration |
| Evaluation tools | Verified source downloads, a .NET runner and Python reports for relevance, HNSW recall, latency, filters and concurrent requests |
| Tests and CI | Request tests, a real-Vespa MAF test and offline checks for the evaluation scripts |
| Documentation | Setup instructions, configuration examples, measured results and their limitations |

The sample defaults to semantic retrieval with Nomic embeddings. It enables HNSW, targets 100 vector candidates and returns up to three passages. To use balanced hybrid search, specify `--retrieval hybrid`. It targets 100 candidates per method, reranks up to 100 hits with equal normalized weights, and uses `tokenize` with optional weakAnd pruning unset. Lexical and RRF retrieval and corpus-specific presets are also explicit choices.

Its function tool and Microsoft's `TextSearchProvider` call the same search code; Vespa handles query analysis, candidate retrieval and ranking. A search with backend errors or incomplete coverage fails instead of supplying partial context to the agent. Saved evaluation configurations keep their own settings.

## Updating an existing sample deployment

Deploy the updated sample application and reseed its three documents to populate the separate title and body fields. Seeding replaces those three known IDs and regenerates their embeddings.

Use a dedicated development instance: `--deploy` replaces the application at the configured endpoint. Consumer applications continue to own their schemas and deployment process.

## NuGet packages

Library code under `src/`, public APIs, package versions and dependency versions are unchanged. `NativeRetrieval` and its settings are internal sample code. The sample and evaluation runner both have `IsPackable=false`.

| Packages | Configured version | README used by a future package build |
|---|---|---|
| Vespa.NET.Models, Vespa.NET, Vespa.NET.Testcontainers | 3.1.1 | Updated root README |
| Vespa.NET.VectorData | 2.0.0 | Existing package README |
| Vespa.NET.AgentFramework | 1.1.0 | Existing package README |

The recommended delivery is a repository update with the existing NuGet versions. Users adopt the search example and configure their Vespa application through the client APIs. A public library API or behavior change would need its own package release.

## Verification

The [filter and load tests](retrieval-load.md) completed 36,936 measured requests across NFCorpus and ArguAna, with no failures or returned documents outside the requested scope. At concurrency 4 and 8, results matched the same preset's serial control, including document IDs and nDCG.

The measurements also show the limits of the broader ArguAna preset: concurrency 8 increased tail latency compared with 4 without increasing throughput. The reports retain that comparison alongside the faster cases. These are local measurements with embeddings already computed.

The [SciFact and FiQA comparison](retrieval-transfer.md) adds 14,220 measured searches with settings fixed before testing those collections. The balanced hybrid scored higher than semantic search on SciFact and lower on FiQA. The report includes the FiQA regression and the relevance and latency of the existing presets. These measurements do not establish a universally best retrieval mode. MS MARCO has not yet been measured with this setup.

The [E5 comparison](retrieval-embeddings.md) adds another 14,220 measured searches on the same collections and configurations. E5 improves semantic and balanced hybrid scores on FiQA, while hybrid still trails semantic. E5 preparation runs outside Vespa; downloaded weights and generated vectors stay in ignored local artifacts.

CI checks all five evaluation scripts offline and runs the MAF integration test against Vespa. It also checks the E5 preparer's CLI without installing PyTorch. Full corpus benchmarks remain opt-in. A remote GitHub CI result is available only after the source has been pushed.

## Review and publication

The review diff includes modified files and new source files. Downloaded corpora, model caches, raw rankings, build outputs and local review exports stay under ignored directories. The repository includes the summarized results and commands to reproduce them.

The existing workflow has three ways to publish packages:

| Trigger | Publication |
|---|---|
| A `v*` tag | Models, client and Testcontainers, using the versions in their project configuration |
| A VectorData version change on `main`, or its manual publishing option | Vespa.NET.VectorData |
| The Agent Framework manual publishing option on `main` | Vespa.NET.AgentFramework |

A normal merge with the current versions runs CI and may upload a VectorData package for inspection. It does not publish a new NuGet version for this update. The MAF live test is an independent job, rather than a dependency of the existing publishing jobs. Creating a `v*` tag would invoke the SDK publication workflow; the workflow does not take its package version from the tag text.
