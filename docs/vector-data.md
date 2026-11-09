# VectorData connector development

See the [package README](../src/Vespa.NET.VectorData/README.md) for installation,
the runnable quickstart, schema requirements, options and Agent Framework usage.
That README is also the documentation embedded in the NuGet package.

## Package boundary

`Vespa.NET.VectorData` is an optional package using only the public `IVespaClient`
API and standalone `Microsoft.Extensions.VectorData.Abstractions`. The base client
and models acquire no connector dependencies. Agent Framework and OllamaSharp
remain consumers in the sample, and Agent Framework is used in integration tests.

The package starts at `1.0.0` and has its own version, independent of the base
client's `3.1.1`. Version changes describe compatibility of the connector API;
they do not mirror SDK or Microsoft dependency versions. Keeping its project and tests here
allows changes to be checked against the client in one build. It can move to a
separate repository when independent maintainers or release scheduling justify
that overhead: replace its client project reference with a NuGet dependency and
move its tests, sample and documentation. No internal client APIs are required.

## Implementation choices

- The provider connects to deployed schemas; lifecycle/discovery calls fail
  explicitly. Schema deployment belongs to the complete Vespa application.
- Filter translation accepts a restricted scalar subset. It rejects lossy casts,
  null/empty-string predicates and array comparisons before any embedding I/O.
  Supported constant promotions retain their actual numeric value.
- Read errors and backend execution failures use contextual `VectorStoreException`
  with the original cause; caller cancellation remains cancellation.
- A write with ignored fields is reported as failure even though Vespa may have
  accepted the other fields. Batches are sequential and do not roll back.
- Results expose cosine scores computed from angular distance, and verify
  document identity. The namespace restriction remains an application contract,
  not a server-side tenant boundary.

## Verify changes

From the repository root:

```sh
dotnet build Vespa.NET.slnx -c Release
dotnet test tests/Vespa.NET.VectorData.Tests -c Release
VESPA_VECTOR_DATA_INTEGRATION_TESTS=1 dotnet test tests/Vespa.NET.VectorData.Tests -c Release --filter FullyQualifiedName~LiveVespaTests
dotnet pack src/Vespa.NET.VectorData -c Release
```

The default tests read actual HTTP request bodies and cover mapping, filters,
numeric conversions, errors, lifetime, cancellation and an Agent Framework tool
loop with deterministic model/embedding doubles. Docker tests are reported skipped
until enabled. Each creates and disposes a dedicated Vespa container; they never
deploy to another running application. Set `VESPA_VECTOR_DATA_IMAGE` to pin an image.

One Docker test checks CRUD, search, score conversion, filtering and Agent Framework,
including preference persistence and recall with fresh agents, updates and user filters.
The other publishes the Agent Framework sample, runs its `--deploy-only` command,
then extracts the README's complete C# example, builds it in a temporary consumer
project, executes it against the deployed 768-dimensional schema, and verifies
document cleanup. Running this test requires the .NET 10 SDK as well
as Docker. The test application's relaxed disk threshold is not applied to the
sample application distributed to users.

The [Agent Framework sample](../samples/Vespa.NET.AgentFramework/README.md) supports
`--deploy --seed` for its first run and `--deploy-only` to prepare a dedicated
development instance without Ollama. Deployment uses the client's existing Admin
API with the checked-in application package; collection lifecycle remains outside
the VectorData provider.

The sample's `--memory` mode registers application-defined tools for saving and
recalling one programming-language preference per user. A stable user/topic key
replaces an earlier preference, and writes wait for search visibility. Run the two
commands in the sample README as separate processes to demonstrate persistence
without replaying the previous conversation. `VESPA_MEMORY_USER` is a demo scope,
not authentication or a tenant boundary.

## Prepare a NuGet package

Pack only the connector; its `Vespa.NET >= 3.1.1` dependency is available on nuget.org:

```sh
dotnet pack src/Vespa.NET.VectorData -c Release -o artifacts/packages
python3 scripts/verify-vector-data-package.py artifacts/packages
```

The verifier checks the package metadata, both target frameworks and the embedded
README. It then runs the existing non-Docker tests against the actual package on
.NET 8 and .NET 10, restoring the client from nuget.org into an isolated cache.
It requires Python 3, both .NET runtimes, the .NET 10 SDK and network access.

The manual **Package VectorData** workflow (`package-vectordata.yml`) performs
these checks and uploads only the connector's `.nupkg` as a downloadable artifact.
It contains no NuGet publication step and is not triggered by a push or tag.
The existing `CI` workflow still builds/tests on a push to `main`; its `v*` tags
publish the three base SDK packages, so do not create such a tag for the connector.

## Publish the connector

The **CI** workflow (`ci.yml`) also supports a manual run. It builds and tests the
solution, packs only the connector, verifies the actual package against the
published client on .NET 8 and .NET 10, and uploads it as an artifact:

```sh
gh workflow run ci.yml --ref main -f publish_vector_data=false
```

After reviewing the release changes, publish with:

```sh
gh workflow run ci.yml --ref main -f publish_vector_data=true
```

Publishing is allowed only from `main`, after both build/test and package
verification jobs pass. The publish job downloads the verified artifact and
pushes that same package to nuget.org without rebuilding it. It uses the existing
`ci.yml` trusted-publishing policy and the organization `NUGET_USER` secret; the
NuGet policy must allow a new `Vespa.NET.VectorData` package, not only new versions
of the base SDK packages. No publication occurs on a normal push, pull request,
or a manual run with `publish_vector_data=false`.

Use `connector-v1.0.0` if tagging this connector release. Do not use a tag starting
with lowercase `v`: the existing `v*` trigger publishes the three base SDK packages.
