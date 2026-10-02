# Proposed entries

Pending rules awaiting operator approval. Not required reading: nothing here is an active rule yet.
Add an entry in the normal format (`### <heading>`, then `**Rule:** … **Prevents:** … **Authority:** …`).
Once the operator approves it, move the entry to its topic file and delete it here; a rejected entry is deleted.

### Judge listbox/option exposure with a verbose a11y snapshot or a DOM role query, never the default snapshot

**Rule:** the default (non-verbose) accessibility snapshot of the browser tooling drops non-focusable descendants of a control, so every `role="listbox"` using the aria-activedescendant pattern (Mantine `Select`/`Autocomplete` or hand-written) shows as an empty listbox while the DOM, `aria-controls`/`aria-activedescendant` and Chrome's real AX tree are correct. Check a verbose snapshot or `querySelectorAll('[role=option]')` before filing an a11y finding. **Prevents:** chasing a phantom "empty listbox" (Mantine `withScrollArea: false` does not change it and regresses the dropdown height). **Authority:** open-items O3/S4 control test (hand-written listbox, focusable vs non-focusable option), 2026-09-28. Target: frontend topic file.

### Measure chat-template token overhead through llama-server, never with a hand-tuned per-template constant

**Rule:** the calibration round measures the tool preamble per model through `POST /v1/messages/count_tokens` (system + messages + tools; on b10201 it equals `/tokenize` over `/apply-template`), with and without a fixed probe tool set, and subtracts what the estimator already charges for the probe. **Prevents:** a constant tuned on one template (Qwen3.8: ~198) under- or over-charging every other template (qwen2.5-0.5b: 84). **Authority:** `LlamaTokenEstimatorCalibrationService`, `TokenEstimatorCalibrationStore.ResolveToolTemplatePreamble`; open-items O1, 2026-09-28. Target: inference-runtime.md.

### A tamper test on an acquisition sidecar must keep it self-consistent, or the registry repairs it first

**Rule:** `GgufModelRegistry.ListAllAsync` rewrites a shape-invalid or revision-inconsistent sidecar from the manifest before `InstalledGgufSnapshotStore` sees it, so a test that changes only the recorded digest never reaches the store's check; recompute `WeightMemberFingerprint`, `RegistrySourceRevision` and `RegistryRevision` when tampering. **Prevents:** a green "rejects a wrong sidecar" test that never exercised the rejection. **Authority:** `InstalledGgufSnapshotStoreTests` (SeedAcquiredAsync); open-items O2, 2026-09-28. Target: backend-tests.md.

### A supervisor caller that must not extend a helper-loaded model's lifetime passes the Transient intent

**Rule:** a llama.cpp caller that loads a model only for a short helper call sets `ResidencyIntent = ModelResidencyIntent.Transient` on its `LocalModelSelection` (or calls the intent overloads of `EnsureRunningAsync`/`TryAcquireInferenceLease`). Every caller that passes no intent counts as Interactive and clears the transient mark for good, and only a process the Transient request spawned is ever marked. **Prevents:** a helper-loaded model holding memory for the full idle TTL and a cap slot, or, the other way, a new background caller silently giving a draft-loaded model the interactive lifetime. **Authority:** `LlamaServerProcessSupervisor` (`RunningProcess.MarkUsed`, `JoinInflightSpawn`), `LlamaServerIdleReaper.IdleTimeToLiveFor`, `SupervisorTransientResidencyTests`; wiki 03 "Eviction & reaper", 2026-09-29. Target: inference-runtime.md.

### A polling UI reads `ILiveMemorySampler`, never a forced hardware-profile refresh

**Rule:** anything that polls memory for display reads `ILiveMemorySampler` (`GET model-fit/resources`), never `IHardwareProfiler.GetProfileAsync(forceRefresh: true)` or `model-fit/hardware-profile?refresh=true`. **Prevents:** a forced refresh per poll spawns a full probe each time and overwrites `HardwareProfiler`'s process-lifetime cache, the baseline model fit and profile invalidation read, with a transient reading. **Authority:** `LiveMemorySampler` (never touches the profiler cache, one shared probe per ~2 s window), `HardwareProfiler.GetProfileAsync`, `LiveMemorySamplerTests`; wiki 03 "Live memory sampler", 2026-09-29. Target: models-and-inference.md.

### A UI that polls image or whisper residency reads `model-fit/runtime-residents`, never a runtime status route or `isBusy`

**Rule:** poll `GET model-fit/runtime-residents` for image and whisper residents (llama.cpp stays on `model-fit/running`), never `GET transcription/runtime` or `GET images/runtime`, and never read an activity snapshot's `isBusy` as "work is running" or "eject allowed": it counts a resident daemon, while the row's `canEject` repeats the gate's eviction-reservation refusal. **Prevents:** a per-poll settings load, installed-runtime read and model recommendation from the transcription status route; an eject button disabled whenever anything is loaded. **Authority:** `RuntimeResidentsService`, `TranscriptionRuntimeService.GetRuntimeAsync`, `ImageRuntimeActivitySnapshot.IsBusy`, `TryAcquireEvictionReservation` on both activity gates; wiki 14 and 24, 2026-09-29. Target: frontend-and-api.md.

### `DelegatingAIFunction` does not hide `ApprovalRequiredAIFunction` from MEAI

**Rule:** a wrapper meant to run an approval-required function without a human (an auto-approval adapter) must override `GetService` to return null for `typeof(ApprovalRequiredAIFunction)`; unwrapping via `InnerFunction` works too. `FunctionInvokingChatClient` detects approval with `GetService<ApprovalRequiredAIFunction>()`, and `DelegatingAIFunction.GetService` forwards to the wrapped function, so a plain delegating wrapper stays approval-required. **Prevents:** every call becoming a `ToolApprovalRequestContent` nothing answers, the run ending as an empty "succeeded" with no invocation and no audit row (inbound agentic MCP runs, I-D12). **Authority:** `McpAgenticToolAdapter` (`GetService` override), `McpAgenticToolAdapterTests.FunctionInvocation_RunsAdaptedTool_InsteadOfRequestingApproval`; ADR 0006 amendment 2026-09-30. Target: agents-and-sandbox.md.

### Sandboxed MCP = self-contained servers; the registration `PATH` replaces the jail `PATH`

**Rule:** a `Sandboxed` stdio MCP server sees only its command's directory, its working directory and `/usr`, read-only: a symlinked command's target and a script's interpreter are not bound, so npm/npx/uv/uvx/mise/nvm/venv installs need `PrivilegedHost`. The jail `PATH` is `/usr/bin:/bin`; a registration `PATH` variable REPLACES it (the chain applies registration env last), it never extends it, and this node's `PATH` never reaches the jail. Only a server that dies before its first message gets a stderr tail. **Prevents:** debugging a sandboxed server by fixing the host `PATH`, or adding one directory to `PATH` and losing `/usr/bin`. **Authority:** `SandboxIsolatedChain.SandboxPath`, `SandboxedMcpStdioTransport.JailSearchPath`, `SandboxedMcpStdioTransportTests`; `docs/security/mcp-trust-tiers.md`. Target: sandbox-and-compute.md.

### Development-host MCP clients need the https origin

**Rule:** on an Aspire development host (not a local launch mode) point an MCP client at the https `endpointUrl` that `mcp/server-key` returns, never the http origin: `UseHttpsRedirection` answers the MCP POST with a cross-port 307. The development certificate is self-signed with `CA:FALSE`, so Claude Code needs `NODE_TLS_REJECT_UNAUTHORIZED=0` (`NODE_EXTRA_CA_CERTS` fails with `UNABLE_TO_VERIFY_LEAF_SIGNATURE`) and Codex needs `SSL_CERT_FILE` with the exported certificate. **Prevents:** a valid key reported as rejected (Claude Code follows the 307 and drops `Authorization`), or a client refusing the cross-origin redirect. `--mcp-only` serves plain http and is unaffected. **Authority:** `Program.cs` (`UseHttpsRedirection` when not local mode), `McpServerApiKeyMapper.BuildEndpointUrl`; live round 2026-09-30; MCP client runbook "Development host (Aspire)". Target: agents-and-sandbox.md.

### Rewrite: "Persisted chat parts are a render/reload record, not model context" (agents-and-sandbox.md)

Two sentences of the current rule are false since the MCP hardening round: "requested only by the integration execution coordinator for a `CallerManaged` session, and only off the FULL read (`GetConversationAsync`)" and "Normal chat never replays parts." Proposed replacement:

**Rule:** a `NodeChatMessagePart` reaches the model only via `ConversationContextBuilder.Build(includeToolHistory: true)` → `ToolExchanges` → `InvocationRunner.BuildChatMessages`. Plain chat (`NodeChatStreamService`) requests it every turn off `GetConversationForTurnAsync`, each result cut to a 2,000-char excerpt and withheld for a cloud model without `AllowCloudModelAccess` (`ToolHistoryWithheld` notice); that read blanks `metadata_json` of compaction-covered rows, so covered exchanges are not replayed. A `CallerManaged` integration continuation uses the full read and replays them. Image parts never replay. **Prevents:** assuming a turn sees tool calls because the SPA renders them. **Authority:** `NodeChatStreamServiceTests` (follow-up, cloud), `IntegrationContinuationTests.ACallerManagedContinuationReplaysTheCallItsResultAndThenTheTurnsText`, `NodeChatTurnReadCapTests`.

### Note: ModelContextProtocol 2.2.0 does not send `notifications/cancelled` when a call's token fires

**Rule:** do not rely on the MCP client to cancel server-side work: cancelling the token passed to `CallToolAsync`/`McpClientTool.InvokeAsync` sends no `notifications/cancelled` (observed on the wire against an in-process server on protocols 2025-11-25 and 2026-07-28; over HTTP the cancellation throws inside the send before the cancellation registration runs). `McpToolCallTimeoutAIFunction`'s timeout therefore abandons the call and audits `timeout` while the server keeps working. **Prevents:** trusting the SDK surface note that token cancellation notifies the server, and writing a test that waits for a notification that never comes. **Authority:** S4 wire observation, MCP hardening 2026-09-30 (PLAN §8); server-side cancel on timeout deferred. Target: agents-and-sandbox.md. Re-check on an SDK bump.

### bwrap `--die-with-parent` under .NET kills the jail when a thread-pool thread retires

**Rule:** never pass `--die-with-parent` to bubblewrap (or set `PR_SET_PDEATHSIG` on any child) from a .NET host: the
kernel binds the death signal to the forking THREAD, and `Process.Start` runs on a thread-pool worker that the pool
retires after idling, so the jail is SIGKILLed at a random moment. Bound a jail's lifetime with the systemd scope
(`KillMode=control-group`, `RuntimeMaxSec`), stdin EOF and the startup reaper instead. **Prevents:** sandboxed MCP stdio
servers dying after 40-180 s with no exit code, no stderr and nothing in the host log (live round 2026-09-30, two
reproductions: the forking `.NET TP Worker` tid and bwrap vanished in the same 10-100 ms sample). **Authority:**
`SandboxIsolatedChainTests.Render_NeverAsksBwrapToDieWithItsParent`; `SandboxIsolatedChain.Render` comment.

### A local-mode host with a v2 `node.key` starts locked: tests and scripts that spawn the real host must unlock it

**Rule:** a desktop or `XE_DATA_DIR` host whose `node.key` is a v2 vault serves only the unlock pre-host until the admin password is given, so any test or script that spawns the real host against such a data directory must pass `XE_ADMIN_PASSWORD` (or `--admin-password-stdin`) for a one-shot, supply an operator secret (`XE_NODE_SQLITE_KEY`) to bypass the vault, or unlock over `auth/vault/unlock`. **Prevents:** a spawned host that reports `XE_READY` and a healthy `auth/status` but never serves the real API, so the test or shell hangs on the unlock page until the 2-minute shell start deadline. `--mcp-key` and `--setup` on a locked vault exit 5 without a password. **Authority:** ADR 0018; `Program.Vault.cs` `UnlockVaultAsync`; `EngineCliProcessTests` locked-vault flows. Target: runtime.md.

### Binary payloads on a request contract are `ReadOnlyMemory<byte>`, never `byte[]` properties

**Rule:** declare image/blob bytes on a public request or response type as `ReadOnlyMemory<byte>?` (as `ImageGenerationResult.ImageBytes` does) and encode with `Convert.ToBase64String(x.Value.Span)`. **Prevents:** a green Debug build that fails the Release build with CA1819 on every `byte[]` property (hit adding `InitImage`/`ReferenceImage`, 2026-10-02). **Authority:** `ImageGenerationRequest` in `Providers.Abstractions/Image/ImageRuntimeContracts.cs`; the Release analyzers.

### Test connection strings may not say `Foreign Keys=True`: turn FKs on with a PRAGMA on the opened connection

**Rule:** a migration test that must run under foreign keys ON executes `PRAGMA foreign_keys=ON` on the migrator's open connection and asserts it reads 1; it never adds `Foreign Keys=True` to the connection string. **Prevents:** `SqliteFileProbeConnectionStringGuardTests` failing the test project, and a table-rebuild migration that was never exercised under the production FK posture. **Authority:** `AddImageEditColumnsMigrationTests`; `AddPlaybookActionsMigrationTests`; `SqliteFileProbeConnectionStringGuardTests`.

### Zod refuses `.pick`/`.omit` on an object that carries a `superRefine`

**Rule:** keep the plain field object as its own exported schema and derive both the refined form schema and any `.pick(...)` subset from it; never call `.pick` on the refined result. **Prevents:** a runtime Zod error (and a typecheck failure) when a feature adds a cross-field refinement to a schema that other modules pick from. **Authority:** `imageGenerationFormFieldsSchema` vs `imageGenerationFormSchema` in `src/features/images/models/ImageModels.ts`; `ImageFormOverrides.ts`.

### A new frontend feature can push the bundle over `applicationJavaScriptBytes`: measure before the final acceptance run

**Rule:** after adding UI plus en/de strings, run `pnpm run build:bundle` and compare `applicationJavaScriptBytes` with `config/bundle-budget.json` before hand-off; a raise needs an approved, measured update, never a silent edit. **Prevents:** `pnpm run acceptance` passing lint, knip and coverage and then failing only at `bundle:check` (image-edit UI added 11,277 bytes against 8,337 of headroom). **Authority:** `scripts/CheckBundleBudget.mjs`; `config/bundle-budget.json`.
### A test that calls an INodeRuntimeSettings synchronous twin from an async method fails only in Release

**Rule:** in an `async` test, read a node setting that has both getters through the async one (`await sut.GetXAsync()`); call its synchronous twin (`sut.GetX()`) only from a synchronous test. A twin with no async sibling (a restart-gated knob) is fine anywhere. **Prevents:** a Debug-green test that the Release analyzers reject (CA1849 / S6966: a blocking call with an async overload inside an async method), found only by the backend gate. **Authority:** reported by the node-settings round 2 slice workers (S1/S2, 2026-10-02); `NodeRuntimeSettingsTests`. Target: backend-tests.md.

### Removing a type can trip the file-placement allowlist, and only a direct test-host run names the entry

**Rule:** after deleting or moving a type out of a multi-type file, run `FilePlacementConventionTests` through the native test host with `--treenode-filter`; `TheAllowlist_HasNoStaleEntry` fails for the now-single-type file, and the gate log shows only the failure, not the stale path. Delete the line from `Architecture/FilePlacementAllowlist.txt`. **Prevents:** a red gate after a clean Debug run, and guessing which allowance went stale. **Authority:** `8655fa109` (BenchmarkKldBaseCache.cs allowance removed), node-settings round 2. Target: backend-tests.md.

### Tests that fingerprint a fabricated eval read the eval model through the same seam the gate reads

**Rule:** when a value an endpoint gate compares against moves from `IOptions<T>` to `INodeRuntimeSettings`, every test that fabricates the compared artifact must build it from the accessor too (store the node setting, or use the stub's `With…`). **Prevents:** endpoint tests that still compile but fail on a fingerprint mismatch, because the fixture kept reading `PlaybookEvalOptions.ModelName` while the gate reads `GetPlaybookEvalModelNameAsync`. **Authority:** `8655fa109` (`AdaptiveMemoryEndpointTests`, `PromoteSuggestedPlaybookActionGateEndpointTests`), node-settings round 2. Target: backend-tests.md.

### Run CommentBudgetConventionTests in the focused Debug pass before the backend gate

**Rule:** after adding comments or XML docs, run `CommentBudgetConventionTests` with `--treenode-filter` in Debug before queueing `scripts/run-backend-tests.sh`: an own-line `//` run longer than two lines, a `<summary>` over 240 characters, or a now-stale `CommentBudgetAllowlist.txt` entry fails it. **Prevents:** a full Release gate spent to learn a comment is one line too long. **Authority:** `8385432c8` (accessor comments trimmed to the budget), node-settings round 2 S1. Target: backend-tests.md.

### FastEndpoints validator 400s carried a trace id that never joined the log

**Rule:** FastEndpoints' default `ProblemDetails` `ResponseBuilder` writes `HttpContext.TraceIdentifier` (the Kestrel connection id), not the W3C trace id the log template prints, so any 400 the framework writes itself needs `FastEndpointsProblemWriter.Build` set as `ResponseBuilder` (`UseProblemDetails` in `Program.cs`). **Prevents:** a user quoting the `traceId` of a validation error and no `[trace:…]` log line ever matching it, while handler-written problems did match. **Authority:** `FastEndpointsProblemWriter.Build`, `ProblemDetailsExtensions.ResolveTraceId`; logging checkup 2026-10-02. Target: runtime.md.

### `MinimumLevel.ControlledBy` must come after `ReadFrom.Configuration`

**Rule:** in `ConfigureServices.AddServices`, the Serilog `MinimumLevel.ControlledBy(NodeLogLevelSwitch.Level)` call goes after `ReadFrom.Configuration` and before `ReadFrom.Services`; never reorder it. **Prevents:** the verbose switch silently doing nothing, because the configured `Serilog:MinimumLevel:Default` is applied last and wins over the switch. The configured `Microsoft*`/`System` Warning overrides are unaffected either way, so nothing else breaks and only the switch test notices. **Authority:** break proof in logging scope C (2026-10-02): with `ControlledBy` moved before `ReadFrom.Configuration`, the Debug switch test failed. Target: runtime.md.

### `StubNodeRuntimeSettings` feature switches default ON: disable a feature in the stub, not only in options

**Rule:** a test that needs a feature off sets it on the stub (`With…Enabled(false)`) or in the stored settings; the nine switch getters on `StubNodeRuntimeSettings` return `true` unless told otherwise, and consumers read the accessor, never `IOptions<T>.Enabled`. Bound any loop the test drives with a `CancellationToken` that fires. **Prevents:** a harness that disables a feature through options alone running the enabled path, and a timer loop under `CancellationToken.None` with an unadvanced `FakeTimeProvider` hanging the gate batch silently. **Authority:** `b202ba93b`, `80d064fda` (`AgentHomeRunRetentionServiceTests`, `SchedulerHistoryRetentionServiceTests`), node-settings tier B. Target: backend-tests.md.

### `NodeSettingsEndpointDtoMapper.ToStoredSettings` must copy every new stored field

**Rule:** `ToStoredSettings` builds a NEW `StoredNodeSettings` record from the request and the current settings; a new stored field needs its `request.X ?? currentSettings.X` line there in the same change, with an endpoint round-trip test that saves a DIFFERENT field and asserts X survived. **Prevents:** a UI save of any unrelated setting writing the record without X, which wipes the stored value back to its seed. **Authority:** `NodeSettingsEndpointDtoMapper.ToStoredSettings`, node-settings tier B S1. Target: backend-tests.md.

### A failed `XE_FULL_ANALYSIS=1` Debug build leaves the previous binary, and the next focused run reports stale green

**Rule:** after a Debug build with `XE_FULL_ANALYSIS=1`, confirm it ended `0 Error(s)` before trusting a focused test-host run; an analyzer failure writes no new assembly, so the run executes the previous one. **Prevents:** a "green" focused run that never exercised the edit, the Debug twin of the Release break-proof trap. **Authority:** node-settings tier B S1 worker report, 2026-10-02; AGENTS.md Validation (deliberate-break proofs). Target: build-and-ci.md.

### `AddServices` registration tests run at `--maximum-parallel-tests 1`

**Rule:** run tests that build the full `ConfigureServices.AddServices` container (Development endpoint registration, hub inventory, feature-switch composition) with `--maximum-parallel-tests 1` when iterating; they share process-global FastEndpoints serializer options and Serilog static state. **Prevents:** intermittent failures in a focused run that look like a product regression and vanish at width 1. **Authority:** memory of FastEndpoints `SerOpts` being process-global; node-settings tier B S1 worker report, 2026-10-02. Target: backend-tests.md.

### A new pre-host file read must skip the test content root

**Rule:** a read of node state before the host is built takes the same `customization is null` guard `Program.cs` passes to `NodeStartupSettings.Read` (`includeLegacyContentRoot`): a test host's content root is the developer's source directory, so a legacy-path fallback there reads whatever file a dev run left behind. **Prevents:** test hosts whose registrations depend on a stray `node-settings.json` in `XE-Local-AI-Engine.Client/`, green on CI and red on one machine. **Authority:** `NodeStartupSettings.Read`, `Program.cs` (`includeLegacyContentRoot: customization is null`), node-settings tier B S0. Target: runtime.md.

### A trust question goes to `IModelTrustResolver`; a routing question to the cloud factory

**Rule:** a gate deciding whether node-local data or execution may reach a model asks `IModelTrustResolver` (`ResolveAsync`, or `Classify` without an async boundary), never `CodexModelCatalog.IsCodexModel` or `IsCloudProviderSelected`; only "which provider serves this send now" asks the factory. **Prevents:** a second locality answer that drifts: `IsCloudProviderSelected` needs a live Codex session and sees no `ext:` id, and three hand-rolled copies once disagreed on Codex ids and on failing open versus closed. **Authority:** `ModelTrustResolver`, `ModelTrustAuthorityGuardTests`; wiki 03 "Trust is not routing", 2026-10-02. Target: agents-and-sandbox.md.
