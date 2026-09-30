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
