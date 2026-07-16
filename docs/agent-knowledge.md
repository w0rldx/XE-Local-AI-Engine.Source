# Agent Knowledge Base

Hard-won rules, invariants, and traps for this repository — the things that are **not** derivable from reading the code, because they encode a bug that was already paid for once.

**Who this is for:** any agent or engineer starting work in a fresh clone. Read this before your first non-trivial change. `docs/wiki/` tells you how the system is *built*; this file tells you how it *bites*.

**Provenance:** distilled from ~135 accumulated session-memory notes spanning 2026-06 → 2026-07, each rule re-verified against the current tree at distillation time. Rules that turned out to be obsolete are recorded in [Stale beliefs](#stale-beliefs-corrected) rather than deleted — an agent that half-remembers the old rule needs to find the correction.

**Maintaining it:** when a fix encodes a rule (not just a patch), add the rule here. Keep entries in the form *imperative rule → the concrete failure it prevents → current file path*. Items marked `(unverified)` were asserted by an older note but could not be confirmed against current code — confirm before relying on them.

---

## 0. Repo orientation

This repo is **standalone**. It was previously a submodule at `~/projects/C0re/Apps/XE-Local-AI-Engine`; it now lives at `~/projects/XE-Local-AI-Engine` with its own remote (`w0rldx/XE-Local-AI-Engine`) and no pointer back to a parent.

Any instruction referencing `C0re.slnx`, `C0re.Client.React.Web`, `C0re.Tests.IntegrationTests`, or a Docker build context rooted at the C0re parent is describing the **old** layout and is wrong today. The real names:

| Thing | Actual name |
|---|---|
| Solution | `XE-Local-AI-Engine.slnx` |
| React app | `XE-Local-AI-Engine.Client.React/` |
| Backend unit tests | `XE-Local-AI-Engine.Tests`, `.Client.Persistence.Tests`, `.AI.Agent.Tests` |
| E2E | `XE-Local-AI-Engine.Tests.E2ETests` (separate lane, see §1) |
| Shared contracts | `XE-Local-AI-Engine.AI.Contracts` (owned in-repo now) |

---

## 1. Build, test, CI, packaging

### A bare `TODO` in a C# comment fails the build

SonarAnalyzer is referenced repo-wide (`Directory.Build.props:39`) with `TreatWarningsAsErrors=true` (`Directory.Build.props:11`), which escalates **S1135** to an error for any comment containing the literal token `TODO` (same class of rule catches `FIXME`/`HACK`/`XXX`).

Phrase deferred work as `// ... follow-up:` or `// Not yet implemented:`. See the live convention at `XE-Local-AI-Engine.Providers.Capabilities/Implementation/HardwareProfiler.cs:245,262`.

### Running backend tests

The three unit-test projects are **TUnit 1.58.x on Microsoft.Testing.Platform** (`<OutputType>Exe</OutputType>`). `global.json` pins `"test": {"runner": "Microsoft.Testing.Platform"}`, which bridges MTP to `dotnet test` — so plain `dotnet test` works:

```bash
dotnet test XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj --no-build \
  --treenode-filter "/*/*/CapabilityReporterTests/*"
```

Use `--treenode-filter`, **not** `--filter`. Filter alternation `(A|B)` silently matches zero tests — filter one class or method at a time.

CI (`.github/workflows/build-and-test.yml`) runs `dotnet test XE-Local-AI-Engine.slnx --configuration Release --no-build --max-parallel-test-modules 1` against the whole solution. It auto-enrolls any project ending in `.Tests`, so a new test project needs no workflow edit. Notes on why it looks like that:

- `--max-parallel-test-modules 1` is required — concurrent suites time out on shared runners.
- The workflow greps output for a `Passed!|Failed!` summary line as a **hollow-gate guard**, catching the silent "zero tests enrolled" failure mode.
- `TZ=Europe/Berlin` is set deliberately to expose non-UTC bugs.
- E2E sets `<IsTestingPlatformApplication>false</IsTestingPlatformApplication>` so solution-wide `dotnet test` skips it; it needs Playwright browsers + a built SPA and runs in `.github/workflows/e2e.yml`.

Both `build-and-test.yml` and the architecture tests are **real PR gates** — they block merges, they aren't advisory.

### Layering is mechanically frozen

`XE-Local-AI-Engine.Tests/Architecture/LayerDependencyTests.cs` uses NetArchTest to freeze dependency direction (providers → Abstractions only; Contracts/Abstractions never reach back up). A structural refactor that breaks layering fails a *test*, not just review.

`.editorconfig:450` sets `dotnet_diagnostic.IDE0130.severity = error` — **namespace must match folder path**, no carve-outs remain.

### OpenAPI → hey-api is the sole REST data layer for React

The generated client at `XE-Local-AI-Engine.Client.React/src/core/api/generated/` is the only sanctioned way React talks REST. Never hand-edit generated files.

`pnpm openapi:check` regenerates and runs `git diff --exit-code` — this is the drift gate. After any backend contract change, regenerate and commit the output *with* the change.

> **Regen trap — this one is invisible.** The throwaway host used for regen **must** run with `XE_LAUNCH_MODE=desktop`, or the spec silently omits every `IDesktopOnlyEndpoint`-gated path (`app-update`, `github-auth`, image endpoints). The generated client then drops them, and you get dozens of phantom `TS2305 no exported member ...` errors. A non-desktop regen is *incomplete without saying so*. Prefer merging new/changed paths into the committed `openapi/v1.json` over overwriting it wholesale.

TanStack query keys generated by hey-api are single-element arrays `[{ _id: operationId, ... }]` — invalidate by **partial-object match**, never `.slice()`.

### Frontend lint

`pnpm run lint` = `tsc --noEmit && node scripts/CheckEventCurrentTargetInUpdaters.mjs && biome lint && stylelint`. It does **not** run `biome format`, so formatting drift won't fail lint. Do not run `biome format --write` across whole directories as a "fix" — it dirties committed files with whitespace churn unrelated to your change.

react-doctor's config must be `doctor.config.jsonc` (comments) — biome parses `.json` strictly and a `.json` with `//` comments fails lint. Its dependency rules are namespaced under the `deslop` plugin, so `ignore.rules` entries need the `deslop/` prefix or they silently no-op.

### Packaging (Velopack)

- `vpk pack` has **no `--pre` flag** — it fails with `'--pre' was not matched`. Prerelease state rides the SemVer suffix in `--packVersion`; `--pre` is only valid on `vpk upload github`. Already correctly wired in `release.yml` — don't "fix" it back.
- The React SPA must be built **before** `dotnet publish`. This is now a hard guard: the `GuardNodeReactBuildPresentOnPublish` MSBuild target errors a webless publish with a "run pnpm build" message, instead of shipping a blank app.
- `publish/README.md` documents both release paths: Velopack release (primary, tag-triggered) and tester zip (manual, `publish/package-rc.sh`).
- Changelog: `cliff.toml` → `RELEASE_NOTES.md` → `vpk pack --releaseNotes`. Notes must exist **at pack time** — there is no notes flag on `vpk upload github`. `(unverified)` Re-uploading assets to an existing release does not update its body; re-releasing needs `gh release delete <ver> --cleanup-tag` or `gh release edit --notes-file`.

### The backend serves the SPA

One Kestrel process serves both API and UI: `app.UseStaticFiles()` + `app.MapFallbackToFile("index.html")` (registered after endpoint mapping) in `XE-Local-AI-Engine.Client/Program.cs`. Don't stand up a second static/node server in the bundle.

---

## 2. Dev environment & local runtime

### This WSL2 box HAS a GPU

**RTX 4080, 16 GB, CUDA toolkit 13.3, compute arch sm_89 (Ada).** Verified live. Any note claiming "WSL has no GPU, GPU work can't be tested here" is **wrong** — CUDA builds, VRAM offload, and `nvidia-smi`-gated paths can all be built and live-tested on this box. cmake/gcc/ninja are present, so a from-source CUDA llama.cpp build works (pass `-DCMAKE_CUDA_ARCHITECTURES=89`).

Don't hardcode the CUDA minor version — it has already drifted (13.1 → 13.3). Read `nvcc --version` if it matters.

### This WSL2 box has no keyring

No Secret Service daemon (`org.freedesktop.secrets was not provided by any .service files`). MSAL/Azure.Identity token-cache persistence throws `MsalCachePersistenceException`, which **Azure.Identity re-wraps as `AuthenticationFailedException`** — so a handler catching only `CredentialUnavailableException` never sees it. When touching Entra/Azure auth here, walk the `InnerException` chain. Consequence: such sign-ins are in-memory-only on this box and don't survive restart.

### `aspire stop` is a no-op — do not trust it

Every Aspire resource runs as a DCP-owned process in its own process group, detached from the AppHost/CLI's process tree, so `aspire stop`'s tree-kill can't reach it (upstream Aspire CLI bug; fixed only in 13.5+, still preview). A killed session therefore leaves an **orphaned `llama-server` holding its port and GPU VRAM** — it runs under `setsid`, so a parent SIGKILL doesn't touch it.

**Use `scripts/dev-stop.sh`** to bring the stack fully down. It targets only same-session/AppHost-descendant processes plus `llama-server` matched under the app's own binaries root, so it won't kill an unrelated Ollama.

A startup reaper (`StaleLlamaServerReaper`, in `XE-Local-AI-Engine.Providers.LlamaServer/Implementation/`) also kills leftovers under the managed binaries root on next launch, so an orphan won't block a restart. `StaleImageServerReaper` does the same for image-gen.

> **Harness gotcha:** never `pkill -f <substring>` where the substring also matches your own command line — the `pkill` process matches itself and dies before reaching the target. Kill by PID, or from a separate shell call.

### Locked runtime decisions — do not "helpfully" reintroduce

- **Docker is gone.** No Dockerfiles remain. Deliberate epic-level decision to drop the dependency (previously used for Ollama hosting + tool sandboxing) in favour of GPU inference with a driver-only footprint.
- **HostAgent is gone.** The gRPC HostAgent client, its "RuntimeManager" UI/hub/endpoints, and the standalone Tray app were all deleted; the Windows-elevation requirement it existed for is now served by an in-app unprivileged process supervisor (Job Object tree-kill on Windows).
  - **Don't confuse this with the worker-hub `Services/Connection/*` subsystem** (`IWorkerHubConnection`) — that's the SignalR cloud-pairing path and is unrelated. Don't delete it by name-matching "connect"/"hub".
- **Tool sandboxing is a supervised process, not a container** — `ProcessSandboxRuntimeProvider` (`ProviderName="process"`), a native process under a node-scoped jail dir. It ships **enabled by default**. It has deliberately **no network isolation** — that's an accepted, documented gap, not a bug to fix by adding Docker back.
- **Ollama was NOT removed** — it is a deliberately kept, gated, opt-in *secondary* provider (`XE_OLLAMA_RUNTIME_ENABLED` / `AddOllamaRuntime` in `AddNodeModelRuntimeExtensions.cs`), with 50+ live call sites on `IOllamaModelService`. What was removed is Ollama from *Aspire's dev orchestration* (no auto-provisioned container in dev). **llama.cpp is the default local runtime** (a supervised `llama-server` process per model, no daemon). Don't strip Ollama code paths.

### llama.cpp binaries

- `LlamaCppReleasePins.PinnedTag` is only the **offline-fallback floor**. The updater resolves a live "recommended" tag from GitHub Releases first, then a cached `installed-runtime.json`, and only then this constant. If you bump it, re-verify the archive layout for that tag.
- **Upstream ships no Linux CUDA prebuilt — Windows only.** On Linux, an NVIDIA box's `GpuVariantSelector` resolves to **Vulkan**, never CUDA. For CUDA on Linux, use either the bring-your-own-binary override (`XE_LLAMACPP_SERVER_PATH` + `XE_LLAMACPP_VARIANT`) or the in-app build-from-source feature — both exist and were live-verified against this box's 4080.
- Verify GitHub asset digests via the Releases API `digest: "sha256:..."` field. There are **no `.sha256` sidecar files** — don't go looking for them.
- Archive layout is not guaranteed to match `ServerRelativePath = build/bin/llama-server`; the resolver falls back to a recursive search by executable name. This was a real shipped bug — don't hardcode the extraction path.
- GPU offload requires `--n-gpu-layers` to actually be emitted for non-CPU variants. It was once silently missing: CUDA initialized, zero layers offloaded, model quietly ran on CPU. If you touch `LlamaServerProcessSupervisor.BuildLaunchSpec`, confirm it's still wired.

### Per-node state must never be written to the install directory

Route every writer of per-node state (settings, encrypted credential stores, hardware-profile cache) through **`INodeDataDirectory`** (`XE-Local-AI-Engine.Providers.Abstractions/INodeDataDirectory.cs`), which resolves to `LocalApplicationData` in desktop mode. Writing to `ContentRootPath` breaks on a self-contained desktop build where that directory may not be writable.

> **The bug this prevents, because it's a good one:** a stale dev `node-settings.json` got committed, the Web SDK auto-globbed it into the publish output as Content, and every fresh install was silently pinned to a nonexistent default model — first-run provisioning skipped its download with no error, just a permanently empty model store. A runtime-written JSON file must never be checked in *or* published as Content.

### Other silent-failure traps

- **A native process-probe with no timeout hangs provisioning forever.** `nvidia-smi`/`wmic` GPU detection once had no deadline; a hung call stalled first-run model provisioning indefinitely with nothing logged. Any shell-out to a native diagnostic needs a per-call timeout *and* an outer deadline that degrades to a safe default (CPU variant).
- **Desktop mode must treat Ollama as absent, not error-worthy.** Any Ollama call path (`/api/show`, `IOllamaApiClient`) must be provider-gated or tolerate connection-refused gracefully. Repeated source of chat failures and noisy stack traces in desktop mode, where no Ollama daemon runs.
- **The desktop loopback port is persisted on purpose** (`DesktopPortStore`, `desktop-port.txt`) so browser-origin-scoped `localStorage` prefs survive a relaunch. Don't revert to a random port per launch.
- Desktop shutdown needs explicit **SIGHUP** (Linux) and **CTRL_CLOSE_EVENT** (Windows, via `SetConsoleCtrlHandler`, blocking ~4s for graceful `ApplicationStopped`) handlers — .NET's default ConsoleLifetime covers neither, and without them console-close orphans `llama-server` again.
- Desktop publish is self-contained single-file but **explicitly not trimmed** (`PublishTrimmed=false`) — trimming breaks EF Core / Serilog / FastEndpoints / MEAI reflection wiring.
- Desktop mode is opt-in via `XE_LAUNCH_MODE=desktop`; off-flag behaviour (headless/Aspire/CI) must stay byte-identical.

---

## 3. Models, inference, retrieval

### Recommendation: walk the quant ladder, never pick one quant

Both advisor lanes rank *every* file in a repo by `QuantLadder.QualityRank` and take the **highest-quality quant at/below the ceiling that fits**, stepping further down toward `QuantLadder.FloorRank` if nothing at ceiling fits (`ModelFitRefreshService.cs:598`, `CatalogRecommendationService.cs:215`).

The old design picked one quant (Q4_K_M else `files[0]`) and dropped the *entire repo* if it didn't fit — so big/new models whose default quant didn't fit never appeared at all, even though a Q3/Q4 variant would have run fine.

### Recommendation ranking is capability-bucketed

Explore lane orders by `EstimatedBytes / 1 GiB` **bucket** → downloads → last-modified → trusted-publisher → repoId. The bucket (rather than raw bytes) stops a trivially-larger model from always beating a newer, more popular peer.

The original bug ordered by `HeadroomBytes` descending — i.e. *smallest-fitting-model-first* — which is exactly why users only ever saw old, weak, tiny models. "Biggest that fits" was the real fix.

Catalog lane orders by tier (S<A<B) → MoE-offload verdict → quant quality → release date → id. "Recommended" = quant at/above Q4_K_M **and** positive headroom; everything else eligible is "CanRun".

### The advisor is two lanes, and one may fail

`ModelFitRefreshService.BuildRecommendationsAsync` concatenates a **catalog lane** (curated `ModelCatalogEntry`, tiers S/A/B) with an **explore lane** (live HF discovery). A catalog-lane failure is caught and degrades to empty — it must never fail the whole refresh, because the explore lane still returns useful results alone.

### MoE models need `MoeFacts`, not naive VRAM math

`MemoryFitEstimator.MoeFacts(ActiveParamCount, ExpertCount, ExpertUsedCount)`; `IsMoe` is `ExpertCount > 0`. The catalog lane prefers curated `ActiveParamsB` over GGUF header expert fields (not every quantized file retains `expert_count`). With only an expert count, `DefaultExpertWeightShareFraction = 0.85` is a deliberately conservative placeholder.

Without MoE modelling, naive total-weights-vs-VRAM math rejects or mis-scores every 2026-era MoE model (Qwen3.5-35B-A3B, gpt-oss-20b, Gemma-4 26B-A4B).

### Multi-part GGUF shards are ONE model

llama.cpp splits (`<base>-00001-of-00003.gguf`) carry a full header only on the *first* split; later splits are headerless tensor continuations and are never independently loadable. `HuggingFaceGgufDiscovery.GroupShards` collapses each group into one candidate (representative = lowest split, size = sum) and drops the group if a merged single-file variant of the same quant exists.

Without this, the advisor treated a lone 0.99 GB tail shard as its own candidate and estimated a 14B model's footprint at ~1.8 GB.

### GGUF filenames from a repo are untrusted input

`GgufFilePath.IsSafeRelativePath` rejects rooted paths and any `.`/`..` segment; `ResolveContainedPath` re-checks containment immediately before any file handle opens. Defense in depth against a compromised repo returning `../../etc/evil.gguf`.

### HuggingFace API facts that bite

Use `filter=gguf`, not `library=gguf`. `gated` is a **string union** (`false`/`null`/`"manual"`/`"auto"`), not a bool. `siblings` gives filenames only — no sizes without `?blobs=true`. .NET **strips the `Authorization` header** across the cross-host CDN redirect to `us.aws.cdn.hf.co`. `mmproj*.gguf` files are vision-projector companions and are filtered out by filename everywhere — never a model candidate.

### Model kind classification

`ModelKind` = Unknown/Chat/Embedding/**Reranker**. Classification order matters: check `IsRerankerName` **before** the embedding-name check, or `bge-reranker-v2-m3` misclassifies as Embedding.

The classification cache is **digest-keyed** — a model whose Ollama-reported capabilities change across an Ollama version bump is *not* re-probed if the digest is unchanged. Stale-capability trap.

### Local-default chat resolution must stay Ollama-blind

`LocalDefaultChatModelResolver` resolves only among installed llama.cpp GGUF chat models, reading the **persisted** `IModelClassificationStore` (not a live `/api/show` probe), excluding Embedding and Reranker kinds. No installed chat model → an explicit `ModelNotInstalled` failure, never a generic "provider unreachable".

Do **not** call `IModelClassificationService.ClassifyAsync` with `Digest=null` on this hot path — it defeats the digest cache and re-probes a possibly-dead Ollama on every send.

### Reasoning ("think") has counter-intuitive Ollama semantics

For a model **lacking** Ollama's `thinking` capability:

| You send | What happens |
|---|---|
| `think:true` or any level string | **400** |
| `think:false` | accepted, but actively **suppresses** reasoning that some GGUF chat templates emit by default |
| *omit `think` entirely* | the template's built-in reasoning runs — this is what you want |

So: non-thinking model + reasoning requested (binary `"on"` **or** graded `low/medium/high`) → **omit** `think`. Reasoning off/unspecified → `think:false`. Thinking-capable models honour `false`/`low`/`medium`/`high` directly.

This logic is **intentionally duplicated across assemblies** — `InvocationAgentFactory.cs:71` and `Invocation/Orchestration/ParticipantReasoningOptions.cs:42`. A change to one must be mirrored in the other. A *new* reasoning-effort value must be added to **both factories plus `ReasoningEffortNormalizer` plus `RuntimePackageValidator` plus `RuntimePackageConfigHash`** — four normalizer sites — or it silently round-trips to null.

### Capacity gate: dispose the reservation

`ICapacityService.DecideAsync` → `CapacityDecision(Verdict, Reason, OllamaEvictionWarning, Reservation)`, verdict ∈ {`Allow`, `QueueSameModel`, `RejectInsufficient`}.

**Only a local `Allow` carries a non-null `Reservation`** (an `IDisposable` into `PendingFootprintLedger`). The caller **must** dispose it when the spawned child exits, or reserved-bytes never comes back down and later spawns wrongly reject. Cloud `Allow`, `QueueSameModel`, and all rejects carry null.

### llama-server spawn invariants

- `--fit on` and any explicit placement flag (`-ngl`/`-ts`/`-ot`) are **mutually exclusive per spawn** — passing both disables `--fit`.
- Every spawn must pin **`--no-warmup --parallel 1`**. Otherwise the default `n_parallel=4` reserves 4× KV cache (making `--fit on` spill weights to RAM even when the model "fits"), and the default warmup run can overrun the ready-timeout, causing a kill/respawn loop.
- KV-cache quantization (`q8_0`) requires `--flash-attn` and matching K/V types, and differs **per backend** (CUDA/Vulkan/HIP). It belongs in the frozen per-machine inference profile, never as a global default.
- A **reranker runs its own dedicated llama-server** (`--rerank --pooling rank`), distinct from an embedding server (`--embeddings --pooling mean`) for the same model — they cannot share a process. `IRerankerClient` degrades to null (falling back to RRF order) on any failure, and must match scores by the **returned** index, not request order.

### llama-server readiness, load lifetime, and eject (Audit-4)

- **Readiness is separated from the stream-idle watchdog.** A cold model load must happen BEFORE the streaming watchdog is armed, or a big model gets killed at the (shorter) `StreamIdleTimeoutSeconds` and can never load through chat. The invocation runner warms a local (llama.cpp) model via `ILocalModelProvider.WarmModelAsync` (`InvocationRunner.PrepareLocalRuntimeAsync`) — reporting `InvocationRuntimePhase` (PreparingRuntime → LoadingModel → Generating) — and only then streams. Cloud/Ollama warm is a no-op. A new `InvocationState.RuntimePhase` field rides both `Clone` methods (the two-Clone gotcha in §5 applies).
- **The readiness timeout is size-aware, not a constant.** The old hardcoded 120 s `ReadinessTimeout` is gone; the supervisor derives the deadline from on-disk model size via `LlamaServerSupervisorOptions.ResolveReadinessTimeout(bytes)` (base + per-GiB extension above a threshold, capped). A readiness **timeout** (process alive but slow) is retried at most `MaxReadinessTimeoutRetries` (default 1) — NOT `MaxRestartAttempts` — so a slow model no longer thrashes ~6 min of kill/reload. A process **exit** during load stays non-retryable (deterministic crash).
- **The spawn/load is DETACHED from the first caller's token.** `EnsureRunningAsync` runs the spawn as a shared, per-key detached task (`_inflightSpawns`) under the shutdown token; a caller cancelling only abandons its `WaitAsync`, the load continues and warms the model for the next send. Single-flight (one spawn per key for a concurrent burst) is preserved.
- **Operator eject is graceful by default (`EjectAsync(model, role, force, ct)`).** It marks the process evicting (no new leases via `TryAcquireInferenceLease`), drains in-flight inference for a bounded `EjectDrainTimeout`, then tears down — returning `LlamaServerEjectOutcome` {`Ejected` | `TimedOutStillBusy` | `ForcedWhileBusy` | `NotRunning`}. A graceful eject that can't drain does **not** kill (returns `TimedOutStillBusy`); `force:true` kills anyway and marks the run operator-ejected. The chat client (`DeferredLlamaServerChatClient`) holds a lease per request and, on a force-eject drop, throws `LlamaServerModelEjectedException` → classified `FailureCategory.Cancelled` (truthful "ejected by operator" message), not a generic provider failure. `EvictAsync` remains the immediate (non-draining) teardown for internal callers (profiling, provider unload). The old ModelFit eject was an unconditional tree-kill; its endpoint doc/copy was corrected.
- **The readiness/liveness probe uses a dedicated, resilience-free HttpClient** (`new HttpClient`, not the app's `IHttpClientFactory`) with a ~1 s per-attempt bound. Routing it through the factory inherited the standard resilience handler's exponential retries, stretching one logical probe to ~10 s and detecting readiness up to ~5 s late. Don't re-route the probe through the shared client.

### Knowledge base / RAG

- **FK cascades don't fire.** The node SQLite connection has no `PRAGMA foreign_keys=ON`, so `ON DELETE CASCADE` never runs. Delete/reindex paths must issue explicit ordered raw-SQL deletes (vectors → chunks [fires the FTS sync trigger] → sections → document → file). An EF-graph delete in a test will **false-pass** without exercising this.
- **Vector search is managed brute-force cosine**, not sqlite-vec — bench-confirmed faster at every corpus size up to 100k rows. sqlite-vec was deliberately deferred (its `vec0` is brute-force with no default ANN index anyway).
- **Embedding-model resolution must be shared.** `EmbeddingModelResolver` resolves configured-exact → first embedding-named installed GGUF → configured-name fallback. The **same resolved instance** must feed both ingest and query, or the vectors are incomparable. Staleness/mass-reset logic must gate on `EmbeddingModelResolution.IsConfident` — resolving during a transient provider outage must never mass-reset a healthy corpus.
- **Hybrid retrieval** = FTS5 BM25 (per-token OR-quoted, not literal phrase match) ∪ vector cosine, fused by Reciprocal Rank Fusion (k=60), then optionally reranked. Every failure degrades to untouched RRF order.
- **Never persist embeddings derived from encrypted/sensitive source text.** Playbook/KB embedding caches are RAM-only, bounded, and keyed by `(id, version, resolved-model-name)` so a model swap can't return a stale cross-model vector.

---

## 4. Agent Mode, MAF, sandbox, cloud providers

### Sandbox: the two guards are mandatory *together*

`ResolveJailPath` (`ProcessSandboxRuntimeProvider.cs:518`) canonicalizes via `Path.GetFullPath` + prefix check — which collapses `..` but does **not** resolve symlinks. A path under the jail can still traverse a symlink planted by a command that ran with the jail as CWD. **Every read/write leg must also pass `EnsureNoSymlinkComponentsUnderJail`** (~:555) before opening.

Host-file reads use a **no-follow open**: `OpenNoFollow` (~:714) P/Invokes raw `open()` with `O_RDONLY|O_NOFOLLOW|O_CLOEXEC`. Do **not** cast `O_NOFOLLOW` to `FileOptions` and pass it to `File.OpenHandle` — the runtime validates the enum and throws `ArgumentOutOfRangeException` on *every* file, not just symlinks. On `fd < 0`, check `Marshal.GetLastPInvokeError()` (errno 40 = ELOOP = symlink leaf).

The **byte-cap re-check must cover post-sizing growth**: size a buffer from `RandomAccess.GetLength`, read exactly that many bytes, then probe one more byte at `length`. A >0 probe means the file grew after sizing — block the whole copy (return null). Never emit a torn or truncated copy.

**Known gap (accepted, Low):** coder-mode's `ExecuteAsync` (backing `list_files`/`search_text` via allow-listed `find`/`grep`) is *not* independently jailed — it relies on `WorkingDirectory` confinement, which does not re-apply the symlink guard. Not model-exploitable today (coder can't create symlinks; host→sandbox copy rejects reparse points), but it widens the moment a write-capable sandbox tool ships.

### Sub-agent spawn: depth cap is structural first

A spawned child is built with `spawn_subagent` **unconditionally stripped from its tool set** (`SubAgentSpawnService.ResolveBindingAsync` → `CurateChildTools`, `SubAgentSpawnService.cs`). The runtime guard (`SpawnContext.Current is { Depth: >= 1 } → reject`) is defense-in-depth for a misconfiguration, **not** the primary control. Never rely on the runtime check alone.

**The child must get its model bound via `ChatOptions.ModelId` at construction.** `RuntimeChatClient` routes the shared `IChatClient` to a provider **per send** off `ChatOptions.ModelId`; a null ModelId silently falls back to the node default. This was a real live bug — the child fell back to Ollama instead of its bound llama.cpp model.

**A profile-bound child consumes the COMPLETE resolved runtime as one unit** — not just its tools. `ResolveBindingAsync` resolves the `ResolvedAgentRuntime` **once** and threads `ResolvedSystemPrompt` (scaffold + persona + injected playbook memory), `ReasoningEffort`, `Skills`, **and** the curated tools into the child. Reading only `AllowedTools` was MED-002: a saved sub-agent silently ran on raw `definition.Instructions` with no scaffold/reasoning/skills — *less* grounding than the anonymous model-id-only path, which already composes the base scaffold. Because a spawned agent-as-tool never receives per-run `RunOptions` (`AsAIFunction` invokes with none), reasoning + skills must be baked into the agent at **construction** — exactly the orchestration-participant shape: reasoning rides `ChatOptions.AdditionalProperties` via **`ParticipantReasoningOptions.Build(effort, supportsThinking)`** (gated on the child model's OWN thinking capability, resolved through `IModelCapabilityResolver` — a non-thinking Ollama model 400s on `think:true`/level), and skills ride an `AgentSkillsProvider` on `ChatClientAgentOptions.AIContextProviders`. Playbook memory **injection** already lives inside `ResolvedSystemPrompt`, so the child inherits it automatically — that parity is desired.

**Deliberately restricted for a child (intentional, not oversight):** (1) `spawn_subagent` is stripped (the structural depth cap above); (2) post-run adaptive-memory **EXTRACTION** is disabled — a child mines no new playbook candidates (injection still rides its resolved prompt). Both are by design; do not "fix" them into parity. The anonymous model-id-only spawn path also stays as-is: raw request instructions, tool-less, no reasoning/skills.

`AIAgent.AsAIFunction()` is GA and **does** forward the outer `CancellationToken` — no linked-CTS workaround needed. Its generated tool input parameter is named **`"query"`**, not `"task"`.

### MAF traps

- **`ChatClientAgentOptions` has NO `Instructions` property** (MAF 1.8 → 1.13). Instructions live on `ChatOptions.Instructions`. Any snippet setting `Instructions=` on `ChatClientAgentOptions` is wrong.
- **Positional ctor order is `(chatClient, instructions, name, description, ...)`** — this has been gotten backwards in-tree at least once (name/instructions swapped). Contract: instructions are delivered **exactly once** via a leading `System`-role seed message, and the `instructions` argument itself must be null on all paths, or you double-send. Because `AgentSkillsProvider` prepends its own preamble, a "no double-send" test must assert **containment**, not exact/null equality.
- MAF delivers `ChatOptions.Instructions` at the raw `IChatClient` boundary via `options.Instructions`, **not** as an injected System message — a fake `IChatClient` in a test must check both places.
- Approval types are `ToolApprovalRequestContent`/`ToolApprovalResponseContent` in the pinned Extensions.AI. `FunctionApprovalRequestContent` (shown in current-looking official docs) **does not exist** at this pin. Gating comes from marking a tool `ApprovalRequiredAIFunction` — **not** from the `UseToolApproval` middleware wrapper (a plain tool under that middleware runs un-gated).
- Agent Skills (`AgentSkillsProvider`) are `[Experimental]` → `MAAI001`, needs scoped pragma suppression.
- Tool-call argument telemetry must **never** log raw arguments — redact to length + SHA-256 12-hex prefix. (An audit found `tool.arguments` leaking into spans at Information level.)

### Cloud providers

**Codex (ChatGPT-subscription OAuth):**
- The backend **rejects `system`-role messages outright** (`{"detail":"System messages are not allowed"}`). `CodexStoreDisabledChatClient.PrepareCodexRequest` strips every System message and folds its text into `ChatOptions.Instructions` — **Codex-side only**; local/Ollama keeps System messages.
- **A local model id must never reach the Codex wire.** The general send path sets `ChatOptions.ModelId` to the active local model, and MEAI's Responses adapter prefers the per-call ModelId over the construction-time one — so a leaked local name gets sent to Codex and 400s. `ApplyStoreDisabled` therefore **unconditionally overwrites** `result.ModelId` with the resolved Codex id (and clears `MaxOutputTokens`). Replicate this pattern for any future cloud wrapper: never trust an inbound ModelId on a boundary that pins a different provider's model set.
- Reasoning effort must **not** ride the Ollama `think` key (`minimal`/`xhigh` 400 Ollama) — it rides a Codex-only `AdditionalProperties["codex_reasoning_effort"]` side channel, because the `ChatOptions` factory is provider-blind. The OpenAI SDK has **no `XHigh`** — UI "Highest" silently degrades to `High` on the wire.
- `store=false` is a **privacy invariant enforced unconditionally** at the wrapper boundary, regardless of caller options. With tool-calling + reasoning + store=false, encrypted reasoning and prior function-call items must be replayed verbatim each round-trip — MEAI's `OpenAIResponsesChatClient` does this automatically for content whose `RawRepresentation is ResponseItem`. Don't hand-roll the replay.

**Azure Foundry / Entra:**
- **Routing is per-request and model-driven**, not connection-presence-driven. `RuntimeChatClient.ResolveActiveClient()` must receive the per-send `ChatOptions.ModelId`. Precedence: explicit Azure-deployment match > explicit Codex session > null/blank (node default) > unknown id (routes local). Getting this wrong sent Azure picks to the local llama-server with "model not installed". Any new cloud provider must participate in this same per-send resolution, not a startup-computed singleton.
- The host allowlist blocks **APIM gateways by default** (`AllowedHostSuffixes` = `.openai.azure.com` / `.services.ai.azure.com` / `.cognitiveservices.azure.com`) — an APIM host is rejected before any auth logic runs unless an operator adds `AdditionalAllowedHostSuffixes`.
- **The Azure OpenAI SDK silently overwrites the `Authorization` header** on the v1 surface, even with a per-call Entra bearer policy — the ctor credential's `ApiKeyAuthenticationPolicy` sits in a fixed per-try slot that runs *after* all per-call policies. Symptom: a cryptic `IDX12741 "JWT must have three segments"`. Fix: pass the bearer policy as the `OpenAIClient(AuthenticationPolicy, options)` **constructor argument**, not `AddPolicy`.
  - Lesson worth generalizing: a construction/DI unit test **cannot** catch pipeline-order header clobbering. Any change to pipeline policies needs an integration test that fires the assembled pipeline through a request-capturing handler and asserts the final wire headers.
- **Client-credentials (app-only) tokens carry `roles`, not `scp`** — a gateway `validate-jwt` policy checking `scp` rejects them even though auth "succeeded" locally. Fix the gateway policy or use the delegated auth-code flow (`ConfidentialClientApplication`, **not** `Azure.Identity.AuthorizationCodeCredential` — that type has no PKCE and no persistable `AuthenticationRecord`).
- **The real Azure auth error is not in `AuthenticationFailedException.Message`** (that's just "ClientSecretCredential authentication failed: ") — the AADSTS code is in the inner `MsalServiceException.Message`. Any sanitizer must walk the InnerException chain.

**MCP HTTP transport** requires `IsHttpScheme` (http/https only — blocks `ftp://127.0.0.1`, `file://…` even on a loopback host) **and** `IsLoopbackHost` (exact-string match against `McpOptions.HttpLoopbackHosts`) at connect time, as defense in depth over the CRUD-layer validation. Do **not** swap the exact allowlist for `IPAddress.IsLoopback` — the strict form was kept deliberately after proving no bypass via metadata-IP, userinfo tricks, DNS-rebinding suffixes, or expanded IPv6.

### SignalR does not replay to late joiners

This is load-bearing anywhere a hub streams run/tool events. The concrete bug: a service published `RunStarted` and began draining events to a group **before** the HTTP response carrying the runId (which the client needs in order to `Subscribe`) returned — so a fast run's events all hit an empty group, and the client saw zero output with a stuck-enabled Cancel button.

**Pattern to replicate for any push hub:** give every event a per-run monotonic `Seq`; keep a bounded per-run event buffer **outside** the live-run dictionary (it must outlive run completion, with a short eviction sweep after a replay-retention window, e.g. 60 s); on `Subscribe`, join the group **then** replay the buffer to the caller; dedupe client-side via a high-water mark + gap set so buffered and live events never double-apply.

**Related:** if "cancel" only cancels the CTS and relies on the normal drain path to publish the terminal event, a model call that never unwinds leaves the UI stuck "running" forever. **Publish the terminal event directly from the cancel handler**; the drain path should just dispose.

### Chat message status is a table-enforced state machine

Two independent writers race one assistant row: the HTTP cancel endpoint and the pump's terminalize/flush. The allowed source statuses per writer intent live in **one** table, `NodeChatMessageTransitions` (`Services/Chat/NodeChatMessageTransitions.cs`), and every correlated UPDATE enforces its set **atomically** via an `AND status IN (...)` predicate — never a read-then-write (`NodeChatMessageCommands.UpdateCorrelatedMessageAsync`). Terminal rows are otherwise immutable, with **one deliberate whitelist**: the pump's true-outcome terminalize (completed/failed/cancelled) may fire from a `Cancelled` source, so an authoritative completion supersedes an optimistic HTTP-cancel marker and a cancel-terminalize over a cancelled row is the idempotent final-content write. `Interrupted` is **not** whitelisted — it can never downgrade a user `Cancelled`. Rules:

- **Cancel / flush / recovery** may fire only from the non-terminal set (`pending`/`queued`/`streaming`). A late flush or cancel against a terminal row is an atomic no-op, not a rewrite.
- **The queued/streaming lifecycle marks are guarded too.** Queued may fire only from `pending`; streaming only from `pending` (platform path — the worker coordinator marks streaming straight off the placeholder, no queued step) or `queued` (local send/regen). This closes the reported race: a cancel landing on the `pending` placeholder **before** the cancellation registration exists can no longer be overwritten back to `queued`/`streaming`. The stream/regen services **check the mark result** — a rejected mark returns the true terminal row, so they emit that terminal SSE and abort instead of running the model into a finalized message (`NodeChatStreamService`/`NodeChatRegenerationService`, both mark sites).
- **Terminalize** derives its allowed sources from the *target* status: non-terminal for `Interrupted`, non-terminal **plus** `Cancelled` for completed/failed/cancelled. `completed`/`failed`/`interrupted` are never a legal source, so a second terminalize is a no-op.
- **The run envelope + the single SSE terminal are built from the PERSISTED winning status** (`persisted.Status`), never the requested one — because the guard may have rejected the write. The ledger can therefore never disagree with the row. Don't "simplify" the pump back to using the requested `terminalStatus`.

---

## 5. Frontend, chat UX, API boundary

### Chat rendering contract

An assistant turn renders as **one ordered `parts[]` array** (reasoning ↔ tool ↔ reasoning → answer), not fixed sections. Do not flatten reasoning into a single string — you lose the wire `sequence` and tool calls render out of order. Renderer: `src/features/chat/components/MessageParts.tsx`, fed by a pure `buildMessageParts()` shared by both the live streaming reducer and the reload-from-DB mapping.

Tool cards use **one** state-driven component (`ToolCallCard.tsx`) for requesting/waiting/received/failed. Don't reintroduce a separate "streaming" vs "final" tool component — that duality was deliberately retired.

Tool args/results render via the shared `CodeBlock` component (`src/core/ui/components/CodeBlock/`) — reuse it rather than adding another highlighter.

Turn metadata (agent name, reasoning effort, tokens/sec, tool parts, duration) rides the existing `metadata_json` blob — additive fields need **no DB migration**. Per-turn setting precedence is `request.value ?? conversation.value ?? default`.

### Error surfacing

A failed assistant turn shows **exactly one** red Alert, driven purely by `hasText(message.error)` — independent of whether partial content exists (`ChatMessage.tsx:165`). Don't duplicate the error into the streaming indicator or the body placeholder; those paths were deliberately stripped of error rendering.

**Toast vs Alert is a deliberate boundary, not an oversight.** Toast (`src/core/ui/notifications/Toast.tsx`) = page-level, transient, mutation-result. Inline `<Alert>` = query load-errors, persistent status banners, empty-state guidance, form validation. Don't migrate the latter to toast.

`i18n.ts:31` sets `interpolation: { escapeValue: false }` — **load-bearing**. Without it, i18next HTML-entity-escapes every interpolated string (e.g. a HuggingFace model id containing `/`) before it reaches JSX, which already escapes text nodes. It is safe, not an XSS reintroduction. If literal `&#x2F;` shows up in the UI again, check here first.

### API-boundary traps

- **Body-less POST endpoints 415.** Any FastEndpoints POST whose data comes only from the route (run-now / enable / cancel actions) is called by the generated client with no `Content-Type`, and FastEndpoints' default `Accepts=application/json` rejects it with **415** — surfacing as an empty, generic error toast. Fix (14 call sites already use it): `Description(x => x.Accepts<TRequest>())` in `Configure()`.
- **Multipart upload 415.** An upload request that only reads the untyped `Files` collection emits an empty OpenAPI `requestBody`, so the generated client sends JSON `{}` → 415. Fix: add a typed `IFormFile? File` to the request DTO so OpenAPI documents `multipart/form-data`, and read `req.File ?? Files[0]`. On the client, `AxiosInstance.ts` sets a global default `Content-Type: application/json` which **silently defeats** hey-api's per-call multipart serializer — uploads must call `axiosInstance.post(url, FormData, { headers: { 'Content-Type': 'multipart/form-data' } })` directly, not the generated SDK method.
- **URL-encoded slashes in path params.** hey-api encodes `/` as `%2F`, and Kestrel leaves `%2F`/`%5C` encoded by design — so a validator regex on the raw route value sees a literal `%` and rejects it. Any endpoint taking a model-name-like value as a **route segment** must decode via `ModelRouteName.Decode` (`Uri.UnescapeDataString`, deliberately not `WebUtility.UrlDecode`, which turns `+` into a space). Endpoints taking the name in a POST body don't need this.
- **int64 wire contract: `long` fields are normalized to `number`, except precision-sensitive seeds which are strings.** Raw hey-api with `validator:true` turns a C# `long`/`long?` (OpenAPI `format: int64`) into `z.coerce.bigint()` — the TS type claims `number` but the runtime value is a `bigint`, and arithmetic throws "Cannot mix BigInt and other types". The fix lives at the spec seam, not the generated client: `FetchOpenapi.mjs` normalizes int64 `format` at spec materialization so ordinary timestamps/durations/counts generate `z.number()`. Precision-sensitive long fields that can exceed 2^53 (sampling/image **seeds**) are instead carried as **strings** on the wire so no precision is lost. Never hand-edit `zod.gen.ts` — correct the contract in `FetchOpenapi.mjs` (or the endpoint's declared type) and regenerate.

### Client conventions

- Modals go through the shared **`DialogShell`** primitive — don't hand-roll a Mantine `Modal`.
- Chat capability flags (file/image attachments) are a **static client-side constant** (`NodeCapabilities.ts`), not server-composed. Don't assume a backend capability endpoint drives chat UI gating.
- Any bounded Mantine `NumberInput`/`Slider` that must distinguish "unset" from "user edited" needs a post-mount `ready` guard before wiring `onChange` to persistence — Mantine fires a **spurious `onChange` on mount** with a default/min value, silently overwriting an intentional "no override". This bit the sampling-options dialog twice.

### Races and flashes

- **Auto-advance must arm on the unmet→met transition**, not fire because the condition is already true on arrival — otherwise a returning user flashes through the step before they can read it. Pattern: `autoAdvanceArmedRef` in `OnboardingProvider.tsx` — reset to false on step change, set true only when the effect observes *unmet*, fire only when armed **and** met.
- Any **globally-mounted** TanStack Query (outside auth-gated routes) must be `enabled:`-gated on having an access token, or it fires pre-login without a bearer, 401s, and sticks in an error state that never recovers after login.
- **react-joyride v3 in controlled mode never emits `STATUS.FINISHED`** — the final Next emits `STEP_AFTER` + `action=NEXT` at the last index. A handler keyed on `FINISHED` hangs on the last step forever.
- **Every SignalR hub must be listed in `vite.config.ts`'s dev WS-proxy allowlist.** One missing hub falls through to the generic `/api` proxy and wedges Vite's *entire* WebSocket proxy — breaking hubs that *are* correctly listed.
- **Push-only (SignalR) terminal states need an explicit query invalidation in the reconcile handler** — there's no REST `onSuccess` to hang it off. Missed once for GGUF-download completion: the installed-models list only refreshed on manual reload.
- **`InvocationState` has two separate hand-rolled `Clone()` methods** (`WorkerEventDispatcher.Clone` and `InvocationResumeRegistry.Clone`), and the *cloned* snapshot — not the live mutated state — is what reaches the chat pump and persistence. **Any new `InvocationState` field must be added to both**, or it silently persists as null despite the dispatcher setting it correctly. This class of bug passes unit tests and is only caught by live verification.

---

## 6. Deliberately NOT built

Don't assume these exist; don't "restore" them.

- **No context-window management** in the agent loop — the entire conversation is replayed verbatim every turn. No token budget, truncation, or compaction in the base invocation path. (`ConversationContextBudgeter` covers *one* growth point and is not a general solution; it does not cover orchestration.)
- **No durable run/resume state** — a run lives in memory; a mid-run restart loses it. The Scheduler's `ScheduledJobRunStore` idempotent-upsert is the template to copy, not something already wired in.
- **No mid-stream retry** by design (a pre-first-token retry + per-model circuit breaker do exist).
- **Approval/HITL machinery exists but is dormant** — only `run_in_agent_home` is gated by it, and that tool ships inactive. There is no general policy layer deciding what needs approval.
- **The scheduler cannot run agents** — its only job handler is a model-recommendation checker.
- **No third sandbox provider.** Only `fake` (in-memory, CI default) and the process-based provider exist. The OpenSandbox self-hosted provider is planned, reviewed, and unbuilt.
- **Playbook/memory retrieval is lexical, not embedding-based** — token-overlap ranking by design. Adaptive-memory injection rides the resolved system prompt (config-hash-safe) rather than a live `AIContextProvider` injection path, which would break resume-safety. Extraction-only use of `AIContextProvider` was the deliberate choice.
- **Adaptive memory is per-agent only** — scoped per `AgentDefinition`, no cross-agent or node-wide sharing.
- **No RAG over chat attachments** — v1 is file-tools-only (agent mode) or inline-text injection with a char cap (plain chat). No image/OCR ingestion.
- **No STT** — voice work is output-only (TTS). Kokoro TTS is **English-only**; all non-English speech falls back to the browser's OS voices.
- Desktop-only settings (ThemeConfigurator, Open Canvas editor) are deliberately not part of the mobile-responsive sweep.

---

## Stale beliefs corrected

Old notes (and agents who half-remember them) assert these. They are **false today**.

| Stale belief | Reality |
|---|---|
| "The WSL dev box has no GPU, so GPU work can't be tested here." | It has an **RTX 4080 + CUDA 13.3**, live-verified. GPU paths are testable here. |
| "Ollama was removed from the app." | Removed only from *Aspire dev orchestration*. It remains a supported, gated, opt-in secondary provider with 50+ live call sites. llama.cpp is the *default*, not the *only*, runtime. |
| "`dotnet test` reports zero tests — run the native test-host exe instead." | Fixed by the `global.json` MTP runner pin. `dotnet test` works, and CI uses it against the whole solution. Native exe still works, but is no longer required. |
| "`release.yml` passes `--pre` to `vpk pack`, breaking prerelease CI." | Already fixed — `--pre` is only on the `upload github` step. |
| "The TOCTOU/no-follow sandbox guards live in `LocalContainerSandboxProvider` (Docker)." | They live in **`ProcessSandboxRuntimeProvider`** (`ProviderName="process"`). Re-check `SandboxProviderSelector.Resolve` before citing a provider name. |
| "Plain `git apply` rejects a `--binary`-diffed patch." | **False** on modern git (2.43+) — it applies fine. Any security control depending on that rejection is unsound. |
| "The advisor runs an approved `llmfit` utility container over gRPC/HostAgent." | That path was built and then **fully replaced** by the in-process `MemoryFitEstimator` + live HF discovery. No Docker/HostAgent in the recommendation path; the `approved_utility_images` table is orphaned. |
| "Recommendation ranks by `OrderByDescending(EstimatedBytes)`." | Superseded by **capability-bucketed** ranking (`EstimatedBytes / 1 GiB` bucket → downloads → date → trust). |
| "`ModelKind` is Unknown/Chat/Embedding." | A fourth kind, **Reranker**, exists — and the reranker name-check must run *before* the embedding check. |
| CUDA toolkit pinned at "13.1"; TUnit at "1.56.x". | Point-in-time snapshots — now 13.3 and 1.58.x. Don't build tooling against a remembered minor version. |
