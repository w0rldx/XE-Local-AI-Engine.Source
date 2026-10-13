# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### The host reaches shared provider rules through Client.Application, never OpenAICompatible.Core (target: build-and-analyzers.md)

**Rule:** `XE-Local-AI-Engine.Client` does not reference `Providers.OpenAICompatible.Core`; an endpoint or validator that needs a shared rule (header names, base-address normalization) calls the `Client.Application` surface that wraps it (`StoredExternalProviderHeader.FindViolations`, `OpenAICompatibleBaseAddress` via the store), never the Core type directly. **Prevents:** a host project reference to the leaf transport layer, which `LayerDependencyTests` rejects after the endpoint is already written. **Authority:** `LayerDependencyTests`; `Client.Application` csproj references.

### Regenerating the committed OpenAPI document needs the live host (target: frontend-and-api.md)

**Rule:** `pnpm run openapi:generate` only re-emits the hey-api client from the `openapi/v1.json` already on disk; it never refreshes `v1.json` itself. After a DTO or endpoint change run `OPENAPI_LIVE_SCRIPT=openapi scripts/openapi-live-check.sh` (regenerate mode) and commit both the document and the client; the default mode only compares. **Prevents:** a "regenerated" client that still carries the old contract, caught only by `openapi:check` in CI. **Authority:** `scripts/openapi-live-check.sh` header; `XE-Local-AI-Engine.Client.React/package.json` `openapi:*` scripts.

### A test-support executable must not name its entry type `Program` (target: backend-tests.md)

**Rule:** an executable under a `Testing.*` project names its entry class after the project (`FakeOpenAiGatewayHost`), never `Program`: the test-category scan covers the `Testing.*` projects and derives its integration markers from untagged top-level types by simple name, so a second `Program` either makes the name ambiguous (never a marker) or, when it uses a hosting primitive, becomes a marker itself, and every test that names the node's `Program` is reclassified. **Prevents:** `TestCategoryConventionTests` reclassifying unrelated tests after a fake gains an entry point. **Authority:** `TestCategoryConventionTests.DeriveMarkers`; `FakeOpenAiGatewayHost` remark.

### A bare test-host run must never rely on TUnit's default width (target: backend-tests.md)

**Rule:** never launch `XE-Local-AI-Engine.Tests` or `Client.Persistence.Tests` bare over the whole module; use the gate, `scripts/run-tests-memory-safe.sh`, or `--maximum-parallel-tests`/`TUNIT_MAX_PARALLEL_TESTS`. TUnit's default is 4 x CPU cores (over a hundred on a many-core machine) and every live `TestServerWebAppFactory` host costs ~200 MB, so an uncapped run reached 115 live hosts, 23 GB RSS, thread-pool starvation (hundreds of `[slow] still running` lines) and 35+ min of wall against the gate's ~380 s. `AssemblyParallelLimit` (both assemblies) is the ceiling of 8 that catches a forgotten flag; a CLI width below it still narrows. **Prevents:** a scratch runner exhausting the box and holding the build lock for an hour. **Authority:** `AssemblyParallelLimit`; `ParallelLimitConventionTests`; `scripts/run-backend-tests.sh` (width comment).

### A NuGet bump of a package with a curated upstream license needs a new exact mapping (target: ci-release.md)

**Rule:** when a dependency round moves a package that appears in `UPSTREAM_PACKAGE_LICENSE_TEXTS` (`scripts/compliance/generate_backend_license_corpus.py`: FastEndpoints, Scalar.AspNetCore, Scrutor, TimeZoneConverter), add a mapping for the new version in the same round: fetch `LICENSE` at the nuspec's `<repository commit>`, store it with its `.source.txt` provenance under `third-party/nuget/upstream/`, drop the old pair, and update the test fixtures. These packages ship MIT with no copyright line, so the generator refuses them without an exact mapping. **Prevents:** a green `build-and-test` followed by a red `dev-build`/release package job, because only `package-velopack.yml` runs the generator. **Authority:** `license_mapping` in `scripts/compliance/generate_backend_license_corpus.py`; `scripts/compliance/tests/generate_backend_license_corpus.test.py`.
