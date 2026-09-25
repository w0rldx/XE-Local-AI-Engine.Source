# Image Generation (stable-diffusion.cpp)

> Reviewed: 2026-09-15 · Code-grounded.

The node generates images **locally** with [stable-diffusion.cpp](https://github.com/leejet/stable-diffusion.cpp). It mirrors the llama.cpp text runtime: the app resolves either a pinned prebuilt or a managed source-built `sd-server`, supervises **one resident daemon per model** on a private loopback port range, and drives generation through a coordinator that serializes work to one job at a time and persists every produced image **encrypted-at-rest**. Nothing about a prompt, an image, or the daemon's HTTP shape ever leaves the node. The feature **ships enabled by default** — there is no off switch in `StableDiffusionRuntimeOptions`; the runtime is always wired.

## Where the code lives

| Concern | Project / path |
|---|---|
| Job coordinator (queue / cancel / replay) | `XE-Local-AI-Engine.Client.Application/Services/Images/Implementation/ImageJobCoordinator.cs` (`IImageJobCoordinator`) |
| Runtime orchestration boundary | `XE-Local-AI-Engine.Providers.StableDiffusionCpp/Implementation/StableDiffusionCppRuntime.cs` (`IImageRuntime`) |
| Process supervisor (spawn / reuse / evict / tree-kill) | `…/StableDiffusionCpp/Implementation/ImageServerProcessSupervisor.cs` (`IImageServerSupervisor`) |
| Binary manager (managed selection or download / verify / cache) | `…/StableDiffusionCpp/Implementation/StableDiffusionCppBinaryManager.cs` |
| Managed source build (probe / fetch / build / adopt / recover) | `…/StableDiffusionCpp/Implementation/StableDiffusionCppSourceBuildService.cs` |
| Installed-runtime state + mutation gate | `…/StableDiffusionCpp/Implementation/StableDiffusionInstalledRuntimeStore.cs`, `…/StableDiffusionCpp/Implementation/ImageRuntimeActivityGate.cs` (`Contracts/IImageRuntimeActivityGate.cs`) |
| Stale-daemon reaper | `…/StableDiffusionCpp/Implementation/StaleImageServerReaper.cs` |
| Runtime endpoints' door onto the provider (endpoint-dependency rule) | `XE-Local-AI-Engine.Client.Application/Services/Images/ImageRuntimeOrchestrationService.cs` — pass-through over `IStableDiffusionCppSourceBuildService`, `IStableDiffusionCppSourceBuildPrerequisiteProbe`, `IStableDiffusionInstalledRuntimeStore`, `IImageRuntimeActivityGate` and `IImageServerSupervisor`; the eight runtime/source-build/job endpoints inject it, never the provider contracts |
| Runtime options | `…/StableDiffusionCpp/Options/StableDiffusionRuntimeOptions.cs` |
| SignalR hubs + publishers | `XE-Local-AI-Engine.Client/Hubs/ImageJobHub.cs`, `…/ImageJobEventPublisher.cs`, `…/StableDiffusionCppSourceBuildHub.cs`, `…/StableDiffusionCppSourceBuildEventPublisher.cs` |
| Local endpoints | `XE-Local-AI-Engine.Client/Endpoints/Images/V1/` |
| React feature | `XE-Local-AI-Engine.Client.React/src/features/images/` |

## Architecture at a glance

```
Operator (React) ──REST /api/local/v1/images/* ──▶ FastEndpoints
                                                        │
                                       IImageJobCoordinator  (Singleton)
                                       · persist Queued job row (image_jobs)
                                       · mint per-job CancellationTokenSource
                                       · run detached through a single-slot semaphore
                                                        │
                                          IImageRuntime (StableDiffusionCppRuntime)
                                          · EnsureRunningAsync(model) → daemon endpoint
                                          · submit + poll job over SdServerJobClient
                                                        │
   IImageServerSupervisor ──spawn/reuse──▶ sd-server child (loopback 127.0.0.1:18200–18299)
                                                        │
   IGeneratedImageStore (encrypted at rest) ◀── decoded image, persisted BEFORE job marked Succeeded
                                                        │
   ImageJobHub (IHubContext) ──push──▶ React useImageJobHub → invalidate TanStack Query
```

## The job coordinator (serialized, singleton)

`ImageJobCoordinator` (`IImageJobCoordinator`, **Singleton**) is modelled on the GGUF download coordinator: a per-job in-flight `CancellationTokenSource` registry, throttled coarse status push, and detached run tasks. The one invariant that shapes everything: **generation is serialized to at most one running job** through a single-slot `SemaphoreSlim`. Extra jobs wait in their run task holding `ImageJobStatus.Queued` and are **never submitted to the runtime** until the slot frees. This bounds the blast radius of a cancel-that-must-kill the daemon to exactly one job.

- **`EnqueueAsync`** persists a `Queued` job to `image_jobs`, mints its token, kicks the serialized worker, and returns the job id. Generation runs **detached** after the call returns (the registry is Singleton so it outlives the request).
- **`CancelAsync`** signals the tracked job's token: a still-queued job is dropped to `Cancelled` **without ever calling the runtime**; a generating job's token is cancelled so the runtime performs the queued-cancel or kill+restart. Returns `false` for an unknown or already-terminal job.
- **`GetAsync` / `ListAsync`** read the persisted status view (newest first); `ListAsync` is paged (`limit`/`offset`) and reports the unpaged total.
- **`DeleteAsync`** removes a terminal job with its images — see [Deleting a job](#deleting-a-job).
- **`SnapshotBufferedEvents`** returns a late hub subscriber's replay log. The coordinator keeps a per-job ordered event buffer (cap 128) that lingers ~5 minutes after a terminal event so a client that connects late can catch up.

Progress carries the coarse status plus the runtime's generation timeline (phase, step counters, seconds per iteration, estimated remaining) — **never the prompt and never a path**. Job state is persisted to `image_jobs` through a fresh DI scope per operation.

Which pushes go out, and which are buffered, follows one rule. A **milestone** — the initial push, every terminal push, and every generation-phase transition — always goes out and is always buffered, so an operator-visible phase change is never delayed and never dropped. A **step tick inside the current phase** is throttled to at most one per second per job (a fast GPU samples several steps per second) and is never buffered: at a couple of pushes a second a minute-long job would evict its own opening events off the front of the 128-entry log, and a late subscriber would replay stale step counters and no phase transitions at all. A cadence-driven sweep evicts expired replay logs, so the last jobs' logs do not linger on an idle node waiting for another job to arrive.

On success the image is persisted encrypted-at-rest **before** the job is marked `Succeeded`.

## The runtime (stable-diffusion.cpp)

`StableDiffusionCppRuntime` (`IImageRuntime`) is the orchestration boundary: it ensures a resident `sd-server` via the supervisor, submits the job and polls it over `SdServerJobClient`, maps coarse status transitions to `ImageGenProgress`, and decodes the base64 image inline on completion. **No sd-server flag, route, or HTTP shape escapes this project** (architecture invariant §3).

**Cancellation is two-mode.** When the token is signalled the runtime asks sd-server to cancel the job: a still-*queued* job cancels cleanly (HTTP 200); a job already *generating* cannot be interrupted (HTTP 409), so the runtime asks the supervisor to **tree-kill + restart** the daemon, dropping the one active job. Because the coordinator serializes to one job, a kill+restart can only ever affect that single job.

The process-wide activity gate (`IImageRuntimeActivityGate` → `ImageRuntimeActivityGate`) also serializes runtime mutation against generation, spawn/readiness, and resident-daemon eviction. A source build or removal returns `409 runtime-busy` while one of those leases is active. Eject the resident image runtime first, then build/remove.

## The process supervisor

`ImageServerProcessSupervisor` (`IImageServerSupervisor`, **Singleton**, `IAsyncDisposable`) owns every resident `sd-server` child. It mirrors `LlamaServerProcessSupervisor` (reduced: no role split, no benchmark profiling, no external-endpoint attach — the image runtime is one resident daemon per model):

- **Reuse-or-spawn** one daemon per model behind a per-model single-flight gate.
- **Readiness-gate** on start by polling `/sdcpp/v1/capabilities`; the daemon binds its socket only after synchronous model load, so a readiness timeout (`ReadinessTimeout`, default 2 min) is a load failure.
- **Loopback port allocation** with collision-retry from the range **18200–18299** — distinct from the llama.cpp range (18100–18199) so the two runtimes never contend for a port.
- **Idle-TTL eviction** via a background reaper (`IdleTimeToLive`, default 15 min) to free VRAM; a reuse-path liveness probe (throttled, timeout-bounded) tears down and respawns a wedged daemon after `MaxReuseLivenessFailures` (default 3) consecutive failures.
- **Per-OS tree-kill teardown** on eviction, abort, and shutdown (Linux process group / Windows Job Object), so no `sd-server` survives a supervisor stop.
- `MaxLoadedProcesses` defaults to **1** — sd-server is VRAM-heavy and typically co-resident with a chat model, so a spawn for a new model evicts an idle LRU daemon first.

Like the text runtime, a `StaleImageServerReaper` runs at startup to reap `sd-server` orphans left by a previous run of **this** app — matched strictly against the app's own binaries root (`{LocalApplicationData}/XE-Local-AI-Engine/stable-diffusion.cpp`) so an unrelated install is never touched. See [Local Runtime & Providers](03-local-runtime-and-providers.md) for the shared supervisor pattern and [Hosting & Deployment](11-hosting-and-deployment.md) for process reaping.

## sd-server flags never emitted

`ImageServerArgumentBuilder` is the only place sd-server startup flag names live, and two Qwen-adjacent settings the
pinned binary offers are left out on purpose:

- **`--flow-shift`** documents itself as `default: auto`, so passing a hand-picked value would replace a model-aware
  default with a guess.
- **`qwen_image_zero_cond_t`** is an *edit*-model conditioning switch (Qwen-Image-Edit); emitting it would change
  conditioning for the text-to-image sets this install path ships. Since `master-913-b167b94` it is no longer a
  standalone flag but a `--model-args` key.

Both stay off until there is a measurement that says otherwise.

## The curated Qwen-Image 2.1 set

The catalog ships `qwen-image-2.1` and no longer ships the original `qwen-image`. The set spans three repositories and
totals about 10 GB:

| Role | File | Repository |
|---|---|---|
| Diffusion | `qwen_image_2.1-Q4_K.gguf` | `leejet/Qwen-Image-2.1-GGUF` |
| Vae | `vae/qwen_image_2.1_vae_bf16.safetensors` | `Comfy-Org/Qwen-Image-2.1` |
| Llm | `Qwen3VL-8B-Instruct-Q4_K_M.gguf` | `Qwen/Qwen3-VL-8B-Instruct-GGUF` |

The weights are under the **Qwen Research License**, which allows non-commercial research use only. The catalog
entry says so in its `license` (`qwen-research`) and `notes` fields, and it is not marked recommended. The 2.1 VAE is
not interchangeable with the original Qwen-Image or Wan 2.2 VAE. Text-to-image needs only `--diffusion-model`,
`--vae` and `--llm`, which the builder already emits for a Qwen set, so 2.1 added no startup flag. Upstream support
arrived in stable-diffusion.cpp PR #1994, so older pins cannot load this set.

The `QwenImage` family defaults are 40 steps, CFG 6.0 and `euler`. CFG and sampler come from upstream
`docs/qwen_image_2.1.md`, and the step count from the QwenLM model card. An import of the original Qwen-Image wants
CFG 2.5, which the operator sets by hand.

## Diffusers-layout files are refused as the diffusion part

sd-server loads one self-describing checkpoint. A Diffusers repo splits the model into per-component folders, and
sd-server exits with `get sd version from file failed` when handed one of those components, after the download has
finished. `ImageWeightLayout.IsUnloadableDiffusionFile` is the single predicate that recognizes such a file:

- a `diffusion_pytorch_model*.safetensors` leaf;
- a shard member (`-00001-of-00003.gguf` or `.safetensors`);
- a `model*.safetensors` beside a `config.json` in the same folder, or anywhere in a repo with a `model_index.json`.
  This rule needs the repo listing, so only inspection applies it.

Repo inspection sets `unsupportedReason: "diffusers_layout_unsupported"` on every such file in the
`GET images/models/inspect` response. The download boundary, `ImageModelFileSetRules.Validate`, refuses a Diffusion
part that matches the name rules with a 400 whose message reads "This repository stores the model in Diffusers
layout, which the local image runtime cannot load. Pick a single-file GGUF or safetensors checkpoint." The rule
applies only to the Diffusion role.

## The rate token is the anchor, not the fraction

`SdProgressLineParser` is the only place sd.cpp's console output format is interpreted. It exists because sd-server's
HTTP job contract has no step, percent or preview field at all — a live verification against the running daemon found
only `queue_position` and the finished image — so the sampler step counter, printed to the process's own stdout and
nowhere else, is the only way to show real progress rather than a spinner.

Three different sd.cpp lines carry an `N/M` pair and only one of them is a sampler step, which is why the parser
anchors on the **rate token** rather than on the fraction:

| Line | What `N/M` means |
|---|---|
| `\|====>    \| 1/8 - 6.34s/it` | the sampler — the one we want |
| `\|####     \| 21/686 - 110.31MB/s` | the tensor loader; N/M is tensors, not steps |
| `generating image: 1/1 - seed 42` | the batch counter |

The batch counter is the dangerous one: it prints `1/1` for every ordinary single-image job, so a parser keyed on the
fraction reads it as "step 1 of 1, complete" and slams the bar to 100% before sampling has even begun. It is covered
by an explicit must-not-match test.

All of this was verified against the build `master-742-1a13107` by running the daemon and hexdumping a real
generation — the same capture that pinned the framing above. At the current pin `master-913-b167b94` the progress
printer is source-identical, and every anchor line still logs at INFO. The tensor-loader line moved from DEBUG to
VERBOSE, and the builder's `-v` still shows VERBOSE.

## Framing sd-server's progress bar

`SdOutputFrameSplitter` cuts sd-server's raw output into frames, and lives apart from `ImageServerProcessLauncher` so
the exact framing sd.cpp emits can be pinned by a fixture rather than only observed in production. A frame ends at LF,
at CR, **or** at the ANSI erase-to-end-of-line sequence — and the last of those is the one that matters.

A hexdump of a real generation against the pinned build shows the progress bar written with a *leading* carriage
return, each frame closed by the erase sequence:

```text
\n \r "  |===>    | 1/8 - 6.34s/it" ESC[K \r "  |=====>  | 2/8 - 4.97s/it" ESC[K ... \n
```

Because the CR *leads*, a frame's text is not terminated by anything until the next frame starts. A reader that splits
only on CR/LF therefore surfaces every step exactly one step late and holds the final step until sampling ends
entirely — a bar permanently one behind, and an ETA computed from stale counters. Treating the erase sequence as a
terminator flushes each frame the instant it is written. All 19 frames of the captured run ended with it, and it was
the only escape sequence anywhere in the capture.

A splitter is not thread-safe: one belongs to exactly one stream's drain loop.

## sd-server's stdout is the progress channel

sd-server's HTTP job contract has **no** step or percent field at all, so the child's drained stdout is the only place
it reports sampling progress. `ImageServerProcessLauncher` therefore offers every frame to `SdProgressLineParser` and
publishes only the PARSED result — phase plus step counters, never the text — to `IImageServerProgressBroker`. That is
what stops a prompt that may sit in a log line from riding the progress path out to the status hub, and it is the same
reason the stdout forward to the app logger is pinned at Debug rather than Information: a normal Information-level
deployment never persists a prompt, while a developer can still opt into the backend/device banner.

Framing is delegated to `SdOutputFrameSplitter` rather than `BeginOutputReadLine`, which cannot surface sd.cpp's
leading-carriage-return progress bar in time — see that type's own remarks.

## Attributing stdout progress to the right generation

The fine phases (`Loading`, `Encoding`, `Sampling`, `Decoding`) are scraped from a daemon's stdout, which says nothing
about *which* job produced a given line. `GenerationProgressTracker` therefore believes an out-of-band observation only
under the rules in `ObserveFine`, keyed on this generation's own polled HTTP status — never on "some job is active".

That matters because an abandoned generation can still be running. The coordinator releases its generation slot in a
`finally` even when the cancel path throws something other than a `StableDiffusionRuntimeException` — an
`HttpRequestException` out of the cancel POST, or a restart refused because the spawn gate is busy — so the daemon may
well still be working on the old job while the next one starts. The progress subscription handle that feeds a tracker
is disposed on **every** exit path, and that is what keeps an abandoned generation's continuing output from ever
reaching the next job's tracker.

## The pinned prebuilt release table

`StableDiffusionReleasePins` is the recommended-pinned acquisition source `StableDiffusionCppBinaryManager` uses when no
managed source-built runtime is selected. The pinned tag is **`master-913-b167b94`** (commit `b167b94`), and assets are
fetched from `https://github.com/leejet/stable-diffusion.cpp/releases/download/{tag}/{asset}`.

SHA256 digests come from the GitHub release-assets API `digest` field, because stable-diffusion.cpp publishes **no**
`.sha256` sidecar files — the digest API is the source of truth. The project ships **rolling** `master-<n>-<sha>`
releases with no semver, moving daily, so bumping the recommended version means re-pinning the tag *and* every hash.

One constraint shapes the whole table: stable-diffusion.cpp ships **no prebuilt Linux CUDA asset**. A Linux NVIDIA box
therefore defaults to Vulkan when a Vulkan device enumerates and to CPU otherwise, enforced by `SdGpuBackendSelector`;
only a validated managed source build can select CUDA there. Windows CUDA additionally needs the separate `cudart-…`
runtime archive, which the Windows-CUDA pin row carries as `StableDiffusionAssetPin.CudartAssetName` /
`StableDiffusionAssetPin.CudartSha256`.

## The bring-your-own sd-server override

`StableDiffusionServerRuntimeOverrideOptions` points the runtime at a locally-built `sd-server` — a Linux CUDA build,
say, for which no prebuilt asset is shipped — instead of the pinned download-and-verify path. It is off by default:
when `ServerPath` is unset, the selector and binary manager behave byte-identically to the pinned path.

**Trust-channel containment.** The override is *operator-trust only*. It is built exclusively from process environment
variables (`ServerPathEnvironmentVariable` / `BackendEnvironmentVariable`) through `FromEnvironment`, which is the same
trust level as the app binary itself. It is **never** bound from an `IConfiguration` section, from the user-editable
node settings store, or from any request DTO: a lower-trust write to the override path would become arbitrary-binary
execution at app privilege. Skipping the network-oriented SHA256 pin is sound only under that containment.

The options type is deliberately dumb. It carries the resolved values and a computed `IsActive` flag and performs no
I/O or path validation in its members — validating the path on disk is the binary manager's job at acquisition time.
The type only decides *whether* an override is configured and *which* backend it claims.

## The runtime HTTP client and its retry contract

`StableDiffusionCppRuntimeServiceCollectionExtensions` registers one named loopback client for job submit, poll, cancel
and readiness, mirroring how llama registers its runtime `HttpClient`. It owns its resilience pipeline outright, because
the default one is wrong here: job submit (`SdServerJobClient.SubmitAsync`) is a POST with **no idempotency key**, so
retrying a submit that failed but was actually received would enqueue a duplicate image job. Under Aspire,
`ServiceDefaults`' `ConfigureHttpClientDefaults` adds a global `StandardResilienceHandler` that retries EVERY method by
default — including that POST.

The registration therefore builds a single POST-safe pipeline: `RemoveAllResilienceHandlers` strips the global handler
(a no-op outside Aspire), and `DisableForUnsafeHttpMethods` narrows retries to safe methods — the GET poll and readiness
calls — while keeping the timeouts and the circuit breaker for every method. This mirrors `AddCentralPlatformResilience`.

## Daemon leases and the teardown races

`ImageServerProcessSupervisor` keeps each resident daemon's lease/eviction state in ONE word, mutated only by atomic CAS:
a value `>= 0` is the count of in-flight generations holding that daemon, and `-1` is a terminal *evicting* latch set by the
idle reaper or the cap evictor. A new lease (`TryAcquireJob`) and an eviction decision (`TryBeginEvict`) transition the same
word, so they can never both win. A plain increment would leave a window in which a lease is granted after the reaper has
read "no active jobs" but before it tree-kills the daemon.

The rules that fall out of it:

- **Acquisition also re-checks identity.** After latching the word, the supervisor confirms the daemon is still the
  registered, live one. A forced teardown (restart, evict, dispose) that removed it between the lookup and the latch would
  leave the lease guarding a dead handle, so the lease is released and acquisition returns null — the caller proceeds
  *leaseless*.
- **The lease spans the whole generation.** `StableDiffusionCppRuntime` holds it across submit → poll → complete, so the
  reaper and the LRU evictor never tree-kill a daemon mid-generation even when the job outruns the idle TTL. Each poll
  `Touch()`es the lease, so the idle window is measured from the last observed progress rather than from submission. A
  leaseless job still runs; the poll loop surfaces any failure through the normal error path.
- **Idle reaping is refused, never forced.** `TryBeginEvict` latches only while no lease is held, and once latched no new
  lease can attach. A generation starting concurrently with a reap therefore either wins the lease first — `TryBeginEvict`
  fails and the daemon is reaped on a later pass — or is refused. A daemon under an active lease is never tree-killed.
- **Tree-kill runs outside the gate, but completes before returning.** A multi-GB kill must not serialize unrelated
  admissions, yet every caller depends on the child actually being gone: the ensure/restart path reaps the outgoing daemon
  and immediately respawns under the same key (at the default cap of one, the replacement's load must not overlap the
  outgoing model's VRAM), and the wedged-daemon and idle-reaper paths must not leave a second child alive against the same
  model files.

Shutdown races with an in-flight spawn are handled the same deliberate way:

- The spawn/readiness window is linked to the supervisor's shutdown token, so a `DisposeAsync` racing a spawn cancels the
  readiness wait and the spawn's own catch tree-kills the launched handle instead of orphaning it. `DisposeAsync` tears
  down only the `_processes` snapshot it sees, and a spawn registers into `_processes` only *after* readiness. A caller
  cancellation unwinds through the same catch.
- A daemon that registered after that teardown snapshot would be left resident, so the spawn tears it down itself. The
  detach/kill pair — or, on a lost removal race, whichever concurrent path won it — owns the kill, dispose and port
  release, so the handle is nulled to stop the catch acting on it twice, and the `ObjectDisposedException` is excluded
  from the error log.
- `DisposeAsync` disposes the per-model ensure gates, so a spawn unwinding on the shutdown-linked token can find its gate
  already disposed. The release is moot at teardown and is swallowed, so the real unwind cause — the
  `OperationCanceledException` from the cancelled readiness wait — reaches the caller instead of a leaked
  `ObjectDisposedException`. A spawn cancelled by `DisposeAsync` likewise unwinds to release its reserved port when the
  admission gate may already be disposed; the disposal teardown reaps every registered daemon's port anyway and no
  concurrent allocator remains, so dropping that release is safe.

**GPU admission.** The spawn-through-readiness window of a GPU-backed image load passes through the SAME process-wide
`IGpuModelLoadAdmission` gate the llama-server supervisor uses, so an image load and an LLM load never race two
`--fit` / free-VRAM reads. The *binary's own* backend decides, because a bring-your-own override may serve a different
backend than the host probe selected; a CPU backend bypasses the gate entirely. The ticket releases on ready or on any
failure through its `using` scope. sd-server has no restart loop, so an admission timeout surfaces straight to the caller.

## Binary provisioning and managed source builds

`StableDiffusionCppBinaryManager` first checks the authoritative installed-runtime record. An active managed runtime is revalidated by backend, path, permissions, and SHA256 before use. Drift tombstones the record and fails closed: the manager does **not** silently fall back to a different prebuilt while the operator-selected managed runtime is invalid. Node Settings exposes eject/remove recovery for that state even when Development Mode is disabled.

Without a managed selection, the manager downloads the **exact pinned prebuilt for the selected backend**, verifies its integrity, extracts it into the per-user cache, and returns its path. A missing GPU prebuilt is not silently replaced by a CPU asset; the selector must explicitly choose CPU. This matters on Linux NVIDIA hosts: the default prebuilt path remains Vulkan when a Vulkan device enumerates, otherwise CPU, because upstream ships no Linux CUDA prebuilt.

On Linux, Development Mode can build CPU, Vulkan, or CUDA from source. The operator can select the engine-pinned official revision, a custom GitHub repository's default branch, or an explicit 40-hex commit with the custom-source risk acknowledgement. Git and CMake run with an allowlisted environment, isolated `HOME`/`TMPDIR`, disabled credential prompts/config rewriting, and shallow fetch-by-SHA. The result is smoke-tested, hash-recorded, permission-hardened, and adopted through a crash-recoverable journal before it becomes active.

Cache/state layout, where `cacheRoot` is `{LocalApplicationData}/XE-Local-AI-Engine`:

```text
stable-diffusion.cpp/
├── {tag}/{backend}/                       # downloaded, hash-verified prebuilt
├── managed/{backend}/{resolvedCommit}/    # adopted source-build tree
├── source-build/
│   ├── .work/                             # disposable isolated build workspace
│   └── adoption-journal.json              # present only while adoption needs recovery
├── installed-runtime.json                 # authoritative active/invalid managed record
└── desired-runtime.json                   # redundant fail-closed recovery intent
```

## Endpoints

Routes under `images/*` (`LocalApiRoutes.Images`), one endpoint class per file in `Endpoints/Images/V1/` — `ls` that folder for the current set:

| Endpoint | Route | Role |
|---|---|---|
| `CreateImageJobEndpoint` | `POST images/jobs` | Enqueue a new generation job (prompt, negative prompt, width/height/steps/sampler). Returns the job id. |
| `ListImageJobsEndpoint` | `GET images/jobs` | One page of persisted jobs, newest first (`limit`/`offset`, with the unpaged `totalCount`). |
| `GetImageJobEndpoint` | `GET images/jobs/{jobId}` | One job's current status view. |
| `CancelImageJobEndpoint` | `POST images/jobs/{jobId}/cancel` | Request cancellation of a tracked job. |
| `DeleteImageJobEndpoint` | `DELETE images/jobs/{jobId}` | Delete a terminal job with its image(s) — rows and encrypted blobs. 409 while the job is still queued or generating. |
| `RetrieveImageEndpoint` | `GET images/{imageId}` | Fetch the produced image bytes for a succeeded job (decrypted on read). |
| `ListImageModelsEndpoint` | `GET images/models` | Installed image models available to the runtime. |
| `DeleteImageModelEndpoint` | `DELETE images/models/{modelName}` | Remove an installed image model. |
| `StartImageModelDownloadEndpoint` | `POST images/models/downloads` | Begin downloading an image model. |
| `ListImageModelDownloadsEndpoint` | `GET images/models/downloads` | Every tracked download — in flight and recently finished — with phase, completed/total bytes, part index/count and a sanitized failure reason. This is how a failed weight download becomes visible instead of a model that never appears. Mirrors `GET model-fit/gguf/downloads`; no path, URL or token is returned. |
| `CancelImageModelDownloadEndpoint` | `POST images/models/downloads/cancel` | Cancel a tracked download. |
| `GetImageModelCatalogEndpoint` | `GET images/models/catalog` | The curated image-model catalog. |
| `BrowseImageRepositoriesEndpoint` / `InspectImageRepositoryEndpoint` | `GET images/models/browse` · `…/inspect` | Hugging Face image-repository discovery and per-repo file inspection. |
| `GetImageRuntimeStatusEndpoint` | `GET images/runtime` | Inspect managed-runtime validity and the process-wide activity gate. |
| `EjectImageRuntimeEndpoint` | `POST images/runtime/eject` | Evict resident `sd-server` processes before a build/remove mutation. |
| `GetStableDiffusionCppSourceBuildPrerequisitesEndpoint` / `GetStableDiffusionCppSourceBuildStatusEndpoint` | `GET images/runtime/source-build/prerequisites` · `…/status` | Probe Linux build prerequisites for the selected backend, and hydrate build status (the hub pushes the rest). |
| `Start`/`Cancel`/`RemoveStableDiffusionCppSourceBuildEndpoint` | `POST images/runtime/source-build(/cancel\|remove)` | Manage the source-build lifecycle and installed runtime. |

All endpoints are loopback/local-only, operator-authenticated, and secret-redacted — see [Security & Privacy](12-security-and-privacy.md). They are surfaced to React via OpenAPI → hey-api; see [API & Hubs](09-api-and-hubs.md).

> **Download progress is polled, not pushed.** Unlike generation jobs, image-model downloads have no hub: `GET images/models/downloads` is the progress surface, and the React model manager polls it while a download is pending. Byte counts plus part index/count are reported, so a multi-part weight download is legible; cancellation goes through `POST images/models/downloads/cancel`.

## Restart and recovery

`DisposeAsync` is the graceful path the DI container prefers: it cancels every in-flight job, then drains the run tasks for a short bound so they can persist their terminal state (`Cancelled`) before the process exits.

Anything that outlives that drain — a hard crash, a kill, a drain timeout — is terminalized by `ImageJobStartupReconciler` on the next boot. The coordinator's in-memory registry does not survive a restart, so without it a row left `Queued` or `Generating` would never be transitioned again and would show as stuck forever. The reconciler marks them `Failed` with a content-free reason (`ImageJobStartupReconciler.InterruptedReason` — never the prompt or a path) and pushes a status event so a connected UI updates.

**Interrupted jobs are never auto-retried.** Image generation is expensive and nondeterministic, so the operator resubmits explicitly. This mirrors the scheduler's stale-run reconciliation in `Program`.

The ordering that makes the pass race-free: migrations are applied in `Program` before the host runs; hosted services then start in registration order; and the web host (Kestrel) starts after all of them. Since the create-job endpoint is the only production enqueue path, reconciliation always completes before a new job could race it.

## Model fit for a diffusion set

`ImageModelFitEstimator` scores a file-set against the host's memory budget before an operator commits to a multi-gigabyte install.

It is deliberately **not** `MemoryFitEstimator.Estimate`. That estimator's whole model is a transformer LLM's: it needs block counts, attention head counts, an embedding length and a llama.cpp quant-byte table to size a KV cache. A diffusion transformer has no KV cache, and a GGUF diffusion file exposes none of those fields, so feeding it here would produce a confident number with nothing behind it. What genuinely reuses is the **hardware probe**: the image estimator shares `MemoryFitEstimator.ResolveFitBudgetBytes`, so an image verdict is scored against the identical budget the LLM advisor uses and cannot drift from it.

**Only the diffusion part is a VRAM cost.** `ImageServerArgumentBuilder.BuildBackendSpec` pins the text encoder and VAE to the CPU on every GPU backend (`diffusion=cuda0,te=cpu,vae=cpu`), so charging a Qwen-Image set's full weight (about 10 GB for 2.1, 18 GB for the original) against VRAM would reject a set that runs fine. In CPU mode there is no such split and the whole set is resident in RAM. The placement is overridable for measurement only: `StableDiffusionRuntime:TextEncoderOnGpu` (env form `StableDiffusionRuntime__TextEncoderOnGpu=true`, default off) moves the text encoder to the GPU (`te=cuda0` / `te=vulkan0`) while the VAE stays on the CPU, and the fit verdict does not follow it. The last measured split for Qwen-Image 2.1 was text encoder 4.3 GB, diffusion 4.0 GB and VAE 0.64 GB.

A set is called a comfortable `Fits` below 80% of the budget rather than tight; the remaining headroom absorbs the runtime's own allocations and the working buffers a diffusion step needs beyond the weights. `Unknown` is a first-class verdict, not a soft "probably fine": `HardwareProfiler` leaves VRAM unmeasured on every non-NVIDIA GPU (and on NVIDIA without `nvidia-smi`), there is no budget to score against, and the CPU budget is the wrong one because the box would run on the GPU.

## Deleting a job

Nothing is deleted until an operator asks. `IImageJobCoordinator.DeleteAsync` is the whole path. The node connection enforces foreign keys, so the `ON DELETE CASCADE` declared on `generated_images` does fire; the explicit ordered delete in `ImageJobStore.DeleteAsync` stays because it also collects the storage paths the blob teardown needs, which no cascade can do.

1. **Refuse a job that is not terminal.** A `Queued` or `Generating` job answers **409** with an `outcome` member of `NotTerminal`; cancel it first (`POST images/jobs/{jobId}/cancel`). The node refuses rather than cancelling on the operator's behalf — the same posture [benchmark project delete](20-benchmarks.md) takes for an active run. Terminal is a one-way door, so reading the status and then deleting needs no lock.
2. **Delete the rows in one transaction** (`IImageJobStore.DeleteAsync`): the job's `generated_images` rows first, then the `image_jobs` row. It hands back the `storage_path` of every blob it unreferenced, or `null` when the job never existed (→ **404**).
3. **Unlink the blobs best-effort** (`IGeneratedImageStore.RemoveJobBlobs`), then the job's now-empty directory. Each path must first resolve **under the image blob root** — the stored `storage_path` is server-computed, but the deletion boundary enforces that itself rather than trusting the column, so a legacy or hand-edited row can never unlink a file the feature does not own; one that escapes is skipped with a warning. A file that cannot be removed is logged and left behind: the rows are already gone, and no sweep will revisit it — an orphaned blob is the accepted cost of never resurrecting a job an operator deleted.

The job's replay log is dropped with it, so a late hub subscriber replays nothing for a job that no longer exists. There is no retention cap and no bulk purge: growth is bounded by the operator, not by a policy.

## React feature

`src/features/images/` (`pages/`, `hooks/`, `queries/`) renders the generation form, the job list, and the produced images. It follows the standard client conventions: TanStack Query for server state, a SignalR hub (`useImageJobHub`) that **invalidates** the matching query on each pushed job event (notification-only; the query refetches canonical state). See [React Client](10-react-client.md).

## Invariants a maintainer must respect

1. **Generation is serialized to one job.** The single-slot semaphore is what makes a kill+restart cancel safe — never widen it without redesigning cancellation.
2. **The image is persisted before the job is marked succeeded**, encrypted-at-rest.
3. **No sd-server flag/route/HTTP shape escapes `Providers.StableDiffusionCpp`** (architecture invariant §3).
4. **Progress never carries the prompt or a path** (privacy §10). The coarse status and the generation timeline (phase, step counters, rate, estimate) are what a push may contain.
5. **The sd-server port range (18200–18299) is disjoint from llama.cpp's (18100–18199)** — keep them from ever colliding.
6. **Managed runtime records are authoritative and fail closed.** Never fall back to another binary after drift without an explicit operator remove/repair.
7. **Eject before build/remove.** Runtime mutation must not race active jobs, spawn/readiness, or a resident daemon.
8. **Nothing outside the image blob root is ever unlinked.** `RemoveJobBlobs` proves containment for every recorded path and for the job directory before deleting either.
9. **Delete rows before blobs, children before parents.** The declared cascade would remove `generated_images` on its own, but `ImageJobStore.DeleteAsync` deletes them explicitly in the same transaction because it must read their storage paths first — the blob teardown has nothing to unlink once the rows are gone.

## Related pages

- [Local Runtime & Providers](03-local-runtime-and-providers.md) — the llama.cpp supervisor this runtime mirrors, binary provisioning, process reaping.
- [Chat](05-chat.md) — the text runtime that typically co-resides with the image daemon.
- [Data & Persistence](08-data-and-persistence.md) — the `image_jobs` table and encrypted image store.
- [API & Hubs](09-api-and-hubs.md) — `/api/local/v1` mapping, SignalR hubs, OpenAPI → hey-api.
- [React Client](10-react-client.md) — TanStack Query + SignalR conventions used by this feature.
- [Security & Privacy](12-security-and-privacy.md) — local-only endpoints, secret redaction, node-local privacy.
- [Architecture Overview](01-architecture-overview.md) · [Project Layout](02-project-layout.md)
