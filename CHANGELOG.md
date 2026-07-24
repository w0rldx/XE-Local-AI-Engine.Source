# Changelog

All notable changes to XE-Local-AI-Engine are recorded here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**Tag convention.** Source tags carry a `v` prefix: `vX.Y.Z-rc.N.M` for release candidates, `vX.Y.Z` for validated
stable releases. The section headings below use the bare version, matching the tag without its `v`.

**Two repositories, one version string.** Source and its `v<version>` tags live in `w0rldx/XE-Local-AI-Engine`; the
built tester artifacts are published as releases on the separate `w0rldx/XE-Local-AI-Engine.Tester-App` repo. A tester
release therefore has no tag in this repo, and vice versa. Tester releases through `0.1.0-rc.4.1` carry **bare** tags
(`0.1.0-rc.4.1`) with `v`-prefixed release names; from `0.1.0-rc.4.2` onward both are `v`-prefixed.

> **This file is hand-maintained.** `cliff.toml` drives git-cliff, which generates `RELEASE_NOTES.md` for the Velopack
> package body — it does **not** generate this file. Update this file yourself when you cut a release; nothing will do
> it for you, which is exactly how it fell five releases behind once already.

### What actually shipped

The tester repo is the authoritative record of what reached a tester. Reconciled 2026-07-24 against
`gh release list --repo w0rldx/XE-Local-AI-Engine.Tester-App` and `git tag -l`:

| Version | Source tag | Tester release (published, UTC) |
|---|---|---|
| 0.1.0-rc.1.0 | `v0.1.0-rc.1.0` | 2026-06-26 |
| 0.1.0-rc.1.1 | `v0.1.0-rc.1.1` | 2026-06-26 |
| 0.1.0-rc.1.2 | `v0.1.0-rc.1.2` | 2026-06-27 |
| 0.1.0-rc.2.0 | `v0.1.0-rc.2.0` | 2026-06-29 |
| 0.1.0-rc.3.0 | `v0.1.0-rc.3.0` | 2026-07-02 |
| 0.1.0-rc.4.0 | `v0.1.0-rc.4.0` | 2026-07-06 |
| 0.1.0-rc.4.1 | **none** | 2026-07-07 |

All seven shipped as GitHub **pre-releases**. Six local tags map 1:1 to a tester release, each published 5–90 minutes
after its tagged commit. The section dates below are the **tester publish dates**, not the tag dates — they differ for
`0.1.0-rc.1.1`, whose commit was tagged at 00:35 local time on 2026-06-27 but published at 22:41 UTC on 2026-06-26.

**One unmatched entry, deliberately not papered over:** `0.1.0-rc.4.1` was published to the tester repo with **no
source tag**, so no commit in this repository identifies it. Its section below is reconstructed from `git log` between
`v0.1.0-rc.4.0` and the version-bump commit `2d8a4ed0`, and is marked as such. The version string is burned. See
[`docs/velopack-release-install-guide.md`](docs/velopack-release-install-guide.md) for how that state arose and which
gates now prevent it. No local tag lacks a tester release.

## [Unreleased]

Targeting `0.1.0-rc.4.2`, which `Directory.Build.props` already reads. **No `v0.1.0-rc.4.2` tag exists yet** —
creating it on HEAD is the operator's release-time step, and the packager refuses to upload without it.
`0.1.0-rc.4.1` is burned: it was published to the tester repo with no matching source tag.

### Added

- Development Mode: a default-on, node-local coding workflow with engine-owned detached Git worktrees, deterministic
  validation, independent review, hash-bound evidence, and an explicit final host apply.
- Generalized llama.cpp **managed source builds** — build a llama-server from source (official or custom repository)
  as a first-class managed runtime, with provenance tracking and lifecycle recovery.
- Token-usage and cost accounting: a usage ledger with a fine-grained provider dimension, an `agents/usage-summary`
  aggregate, server-computed `EstimatedCostUsd` per bucket/provider/total, an approximate default rate table, and
  operator-editable per-model rate overrides with a usage dashboard.
- Node-wide **tool-approval policy** (OPP-03): a `ToolCategory` taxonomy on tool offers, `IToolApprovalPolicy` with a
  permissive no-op floor, a `NodeToolApprovalPolicy` backed by node settings, tool risk-class surfaced on the agent
  tool catalog, and an audit row plus metric per approval decision. The compose is strictly tighten-only — a policy can
  add approval, never waive it — and an `Unknown` category fails closed.
- Interactive tool approvals in chat: pending approvals surface on the local stream with approve/deny controls on the
  tool card, resolved through a loopback endpoint.
- Scheduler can run a **saved agent** on a schedule (`RunSavedAgentHandler`): node-local models only, capacity-gated,
  with approval-required tools stripped for unattended execution.
- Knowledge base: plain chat can be grounded on the knowledge base with a "Use Knowledge Base" toggle and a collapsible
  Sources strip, clickable source cards that open the document drawer, token-aware chunk sizing keyed to the embedding
  model's context, score-aware hybrid fusion on the no-reranker path, one-click download of a recommended
  cross-encoder reranker, and last-known-good RAG results served with explicit disclosure.
- Encryption and privacy: chat message content and metadata are encrypted at rest; conversation retention is
  configurable and **off by default**, and a purge removes the full conversation footprint.
- Runtime hardening from the fourth and fifth audits: a runtime **device audit** that detects a silent GPU→CPU
  fallback, a process-wide **GPU model-load admission gate** shared by the LLM and image supervisors, a central
  `llama-server` launch policy (deterministic `-c`, GPU KV-cache/flash-attention defaults with a one-shot safe
  fallback, CPU thread policy), and the effective context window probed post-readiness and propagated into both
  context budgeters and the chat context meter.
- Observability: TTFT and pre-spawn metrics, a cancelled-turn counter, first-run spans, invocation trace IDs, and
  persistent file logging with supervisor lifecycle logs.
- Cloud providers: Entra ID authorization-code sign-in (confidential client + PKCE) and the OpenAI v1 API surface for
  Azure Foundry connections.
- Model advisor: a curated model catalog with MoE-aware fit, sectioned recommendations UI, and an advisory quantized-KV
  (Q8_0) estimate on catalog recommendations.
- Agent harness: a base instruction scaffold, `TurnPolicy` with a budget hard-stop, deterministic input-context
  budgeting per turn, tool-argument validation with a model repair loop, and stream-idle/tool-call timeouts with a
  pre-first-token retry.
- `node.sqlite` is snapshotted via `VACUUM INTO` before pending migrations are applied.

### Changed

- Release packaging and update-channel validation hardened in `publish/package-tester-win.ps1`.
- The orphaned `approved_utility_images` store, interface, and table were removed (the last residue of the retired
  container-based model-fit path); the table is dropped by migration.

### Fixed

- **llama.cpp official source builds answered 409 on every attempt.** `LlamaCppSourceBuildRequestValidation.Normalize`
  ran at three layers and was not idempotent: for `source=official` the first pass wrote the server-selected
  repository, which the strict "the official repository is selected by the server" rule then rejected on the second
  pass. Custom-source builds were unaffected. Normalization is now idempotent and the endpoint no longer pre-normalizes.
- A non-`ProblemDetails` error body rendered an **empty** toast — `ApiError` discarded the real reason because it read
  only `detail`. It now resolves `detail → message → title`.
- Source-build lifecycle: relocated source runtimes are preserved, source selection provenance is retained, runtime
  mutations are serialized, the mutation gate is released during readiness, and the runtime fails closed on drift.
- A DI factory read node settings unguarded at construction, NRE-ing every host-based test; `Load()` is now null-guarded.
- Local-default chat resolves the concrete model through the installed mirror.
- Terminal image-job replay logs are evicted on an idle node, and the CUDA build log is bounded.
- Invalid scheduler `reasoningEffort` overrides fall back to the agent's own effort.
- API responses are validated in the attachment and onboarding queries.
- Dependency maintenance: `sharp` advisory patched, Aspire SDK aligned, third-party license manifest refreshed.

### Performance

- Chat: long threads are windowed in `ChatMessageList` (`@tanstack/react-virtual`), per-turn invalidations are scoped
  to what a turn actually changes, streamed content is backed by an immutable accumulator (removing an O(n²) clone
  cost), and redundant per-turn work was removed (one agent-definition read, cached provider resolution, memoized
  token estimates).
- SignalR: one refcounted connection shared per feature hub.
- Knowledge base: concurrent search arms with batched hydration/expansion, one query per document for context
  expansion, and vector-search row IDs materialized only for accepted candidates.
- Routine HTTP request logs demoted to Debug.

### Testing

- `TestingWebAppFactory` no longer leaks its temp SQLite/nodedata/wwwroot artifacts, and
  `scripts/run-tests-memory-safe.sh` runs the heavy module in fresh per-namespace processes to work around the
  `WebApplicationFactory` host-retention leak (an upstream framework characteristic, test-only).

### Known issues

- **There is no CI.** GitHub Actions is disabled on this repository — `build-and-test.yml` and `release.yml` are both
  `disabled_manually`, `e2e.yml` was never registered as a workflow, and the repo has 6 runs, 6 failures and 0
  successes in its entire history. The only enforced quality gate is `publish/package-tester-win.ps1`, which runs the
  frontend and backend gate sets on the packaging machine at release time. Between releases, validation is manual.
- No backend hard coverage gate (no baseline yet, so a real threshold would either block the RC or be hollow). The
  frontend coverage thresholds are enforced only by `test:coverage:check`, which the packaging script runs.
- Local-only mode (no `CentralPlatform:BaseUrl`): cloud services remain registered and fail with a generic HTTP error
  if invoked directly; the UI surface is capability-gated off. Proper fail-fast messaging is still outstanding.
- Windows AMD/Intel GPU: GPU hardware is not detected on Windows for AMD or Intel GPUs; these configurations fall back
  to CPU mode. NVIDIA on Windows is unaffected. A DXGI VRAM/vendor probe is a deferred follow-up.
- Upstream ships no Linux CUDA prebuilt llama.cpp binary. On Linux an NVIDIA box resolves to Vulkan; CUDA requires
  either the bring-your-own-binary override or the in-app build-from-source runtime.
- The `main` update channel is intentionally inert (`appsettings.AppUpdate.main.json` keeps its `REPLACE_*`
  placeholders). Distribution is tester-only by decision, not by oversight.
- The agent process sandbox (`ProcessSandboxRuntimeProvider`) enforces the working-directory jail, a scrubbed child
  environment, per-command timeouts, and captured-output byte caps, but provides no network isolation and no
  CPU/memory/PID limits; a sandbox request for either is rejected fail-closed rather than silently ignored, and
  OS-level isolation is deferred to a future provider.
- All local chat catalog tools ship auto-execute this RC (`LocalAgentToolRegistry.CatalogRequiresApproval` is
  `false`); the node-wide per-agent tool-approval policy (OPP-03) still applies on top where configured.
- ModelFit's Benchmark mode is inert scaffolding: a refresh request for it is rejected with "Benchmark refresh is not
  yet enabled"; only the Recommend path is live.
- Voice output ships opt-in and off by default, and the remote-TTS fallback in the voice manifest contract is
  deferred (unbuilt; always `null` on the manifest).
- Image-model downloads report no progress and cannot be cancelled once started — the endpoint returns 202 and the
  transfer runs to completion in the background regardless.
- Image generation has no Linux CUDA path: stable-diffusion.cpp ships no Linux CUDA prebuilt, so a Linux NVIDIA box
  generates images via Vulkan (or the CPU floor), unlike chat's CUDA source-build option.
- Chat retention policy is configured only via `appsettings` (the `ChatRetention` section); there is no UI to view or
  change it yet.

## [0.1.0-rc.4.1] — 2026-07-07

> **Reconstructed section.** Published to the tester repo only; there is **no `v0.1.0-rc.4.1` tag in this
> repository**, so no commit identifies what shipped. The contents below are inferred from the 15 commits between
> `v0.1.0-rc.4.0` and the version-bump commit `2d8a4ed0` and may not match the artifact exactly. The version string is
> burned — do not reuse it.

### Added

- Entra ID authentication mode for the Azure Foundry provider, with matching UI in cloud settings.

### Fixed

- Entra ID concurrency and device-code durability in the cloud providers.
- `EntraSignInMethod` string-to-union casts guarded in cloud settings.
- The cloud-settings save toast claimed credentials were cleared for keyless auth modes.

## [0.1.0-rc.4.0] — 2026-07-06

### Added

- Versioned Windows tester pack/upload script (`publish/package-tester-win.ps1`) — from this release on, the canonical
  release path.
- Uninstaller scripts shipped in the tester bundle: `publish/windows/uninstall-xe-local-ai-engine.ps1` (PowerShell 5.1)
  and `publish/linux/uninstall-xe-local-ai-engine.sh` (POSIX `sh`). Both stop the node and the `llama-server` /
  `sd-server` children it spawned — matched strictly by executable path under the app's own data dir, so an unrelated
  `llama-server` is never touched — then, after an explicit confirmation, delete **only** the per-user data directory.
  They deliberately do **not** remove application binaries: a Velopack-managed install is detected and left to
  Velopack/the OS uninstaller, and portable-zip users delete the unzipped folder by hand. Both refuse to run elevated
  and support `--dry-run` / `--keep-data` / `--yes`.
- Speculative decoding for `llama-server` with operator-settable modes and draft-model support.
- Prompt-cache prefix reuse for chat servers.
- Local cross-encoder reranker stage in the knowledge base.
- Persistent file logging and supervisor lifecycle logs; sandbox capabilities reported honestly.
- Multi-language voice output via OS speech voices, and About / Report-a-problem in the mobile navigation drawer, with
  mobile logout in the drawer and auto-fullscreen dialogs.
- `scripts/dev-stop.sh --all` sweeps every running AppHost stack.
- Agent and knowledge-base tool-result budgets, a pinned iteration cap, and a model-derived embedding dimension.

### Fixed

- Mobile responsiveness for the chat view and app-wide layouts.
- Markdown is stripped from voice-mode TTS output.
- GPU free-VRAM capacity fit, FTS hybrid recall, and image-runtime reliability.
- The release workflow seeds the SPA `.env` from the template; canonical app title.
- New Sonar/CA analyzer errors after a package update.

### Performance

- De-quadratic per-token streaming hot path on both ends.

## [0.1.0-rc.3.0] — 2026-07-02

### Added

- **Knowledge base / RAG**: persistence entities and blob store, the ingestion + embedding pipeline, hybrid retrieval
  (FTS5 BM25 ∪ vector cosine fused by Reciprocal Rank Fusion), endpoints and a SignalR hub with explicit delete, MAF
  tools, and a React Knowledge page.
- **Local image generation** (stable-diffusion.cpp): image model registry, store, binary manager and migration; an
  `IImageRuntime` sd-server adapter with supervisor and job client; an image job coordinator with encrypted store and
  SignalR hub; and a React image-generation feature with form, job list and gallery. Verified end-to-end in-app.
- Custom Azure Foundry request headers with an operator host allowlist, plus React UI for custom headers, host
  suffixes, and a managed-identity egress warning.

### Fixed

- Knowledge base: three embedding gaps closed (out-of-box embedding resolve, GGUF model kind, chat-model exclusion);
  the resolved embedding model name is used as the vector identity; staleness is guarded against a non-confident model
  resolution so a transient outage can't mass-reset a healthy corpus; terminal indexing status reflects live; the
  knowledge-base SignalR hub WebSocket is proxied in dev.
- Open Canvas delivered run events to late subscribers via an event buffer and replay, fixing the subscribe-after-
  publish race that left the UI with no output and a stuck Cancel button.
- Chat surfaces a failed stream instead of hanging on a hub error.
- `llama-server` fails fast when it exits during model load.
- The image GPU backend selector falls back to CPU when no Vulkan device is present.
- The loaded-models poll no longer logs Ollama-unreachable as a warning.

## [0.1.0-rc.2.0] — 2026-06-29

### Added

- Azure AI Foundry as a cloud chat provider.
- Linux CUDA support for `llama-server` by two routes: a bring-your-own-binary override and an in-app
  build-from-source self-managed runtime. (Upstream ships no Linux CUDA prebuilt.)
- AgentHome enabled with the process sandbox.
- Local-only frontend error-snapshot capture with a diagnostics panel and W3C trace correlation.
- Stable DataProtection key ring, with `node.key` DPAPI-wrapped on Windows.
- Open Canvas preview: n8n-style drag-from-palette node creation.

### Changed

- Ollama isolated behind a single seam, misleading symbols renamed, and the sandbox default hardened.
- ModelFit DTOs split by concern and scheduler wire enums isolated.

### Fixed

- The refresh endpoint is rate-limited, stopping a pre-auth 401 retry storm.
- `LocalChat.DefaultModel` set to the GGUF first-run id.
- The llama.cpp runtime is flagged for update only when the installed version is older than the recommended one.
- The voice manifest mock is rejected in production builds.

## [0.1.0-rc.1.2] — 2026-06-27

### Added

- **Inference optimizer**: an `inference_profiles` store with benchmark metric columns, a profile resolver with
  invalidation and machine key, a real `--list-devices` available-VRAM probe, GGUF Mixture-of-Experts detection, a
  supervisor profiling seam with fit-params parser, an explore/benchmark/freeze orchestrator, operator endpoints, a
  React panel, and profile-driven `llama-server` launch arguments.
- Model-fit advisor: quant-ladder fit with recency and popularity ranking, and a recommended GGUF quant variant in the
  download picker.
- Auto-generated changelog for Velopack releases via git-cliff.

### Changed

- Quant quality unified into a single `QuantLadder` source of truth.

### Fixed

- Orphan-proof `llama-server` teardown and a reliable `dev-stop` (`aspire stop` is a no-op on this stack — every
  resource is a detached DCP-owned process, so a killed session otherwise leaves a `llama-server` holding its port and
  GPU VRAM).
- Every `llama-server` spawn pins `--parallel 1 --no-warmup`; the default `n_parallel=4` reserved 4× KV cache.
- Stream watchdog timeouts raised for slow large local models.
- Local GGUF model deletion goes through the GGUF store rather than a dead Ollama path.
- In-flight TTS chunks are cancelled on barge-in so Stop actually halts audio; the pointless tap-to-enable audio banner
  was removed.
- `.sh` files keep LF endings on Windows; git-cliff is called directly in the runbook.

## [0.1.0-rc.1.1] — 2026-06-26

### Added

- Welcome-screen language picker with translated tour buttons.
- Voice audition from node settings before choosing a voice.

### Fixed

- `vpk pack` has no `--pre` flag — the invalid argument was dropped from the pack step (it is valid only on
  `vpk upload github`).
- Body-less POST job-action endpoints returned 415; they now declare `Accepts<TRequest>()`.
- Scheduled runs notify on completion.
- The selected voice drives the chat engine, and Web Speech honours `voiceId`.
- Kokoro `wasmPaths` set via the env accessor rather than `env.backends`; benign onnxruntime warning noise silenced.
- `RootErrorComponent` moved out of the routes directory.

## [0.1.0-rc.1.0] — 2026-06-26

Initial developer release candidate, targeting external Windows 11 testers via a self-contained portable bundle. There
is no OS-native installer: MSI/deb/rpm packaging is deferred, and the runtime is self-provisioning (it downloads its
own llama.cpp binary and GGUF models into the per-user data dir on first launch).

### Added

- Agent Mode foundation: AgentHome write-back loop, Playbook phases P1–P5 (manual, feedback, analysis, eval-gate,
  monitoring + retrieval), embedding-cosine ranker, harvest-golden.
- Agency-agents starter pack: 14 MIT-licensed persona templates importable via the Operator UI.
- Codex OAuth cloud chat provider (ChatGPT-subscription sign-in) with tool-calling support.
- Scheduler foundation (Quartz.NET) with a React management UI and realtime SignalR push.
- Model-fit recommendations: in-process hardware/GGUF estimator, Quartz-driven refresh, React UI.
- Model type classification (`Unknown`, `Chat`, `Embedding`, `Reranker`) with persisted detection and operator
  override, plus capability gating (thinking/tools auto-detected; Ollama 0.30.5 pinned).
- Unified dialog system (`DialogShell`, `useUnsavedChangesGuard`, `MarkdownEditorField`).
- hey-api single-source-of-truth migration: the backend OpenAPI spec drives every React REST client.
- Chat ordered-parts rendering (reasoning ↔ tool ↔ answer in one ordered list).
- Chat advanced sampling options (dev-gated per-send `temp`/`top_p`/`min_p`/`num_ctx`).
- Conversation title encryption (interceptor plus additive migration).
- Table pagination (`useTablePagination` + `TablePaginationFooter`, default 25 rows).

### Removed

- The obsolete `tools/installer` bundle project (a .NET installer CLI) was deleted on 2026-06-19, before this tag. As
  of this release there is **no installer of any kind** — the distribution vehicle is a portable bundle plus a launcher
  script. Uninstaller scripts arrive at 0.1.0-rc.4.0.

### Fixed

- Non-UTC timezone failure in `CapabilityReporterTests` (`TZ=Europe/Berlin` in the test step).
- Chat errors shown once as an alert, surviving regenerate and reload.
- Per-turn reasoning effort preserved across model switches.
- HuggingFace model delete 400 — slash encoding in model-name path endpoints.
- OpenAPI client drift (a one-line Biome formatting diff in `client.gen.ts`).

### Known issues

- Conversation titles are encrypted at rest; pre-existing titles (including operator renames) are re-derived from the
  first user message by a one-time startup backfill. Custom renames from before the migration are not preserved, and
  conversations without a user message keep a `NULL` title.
