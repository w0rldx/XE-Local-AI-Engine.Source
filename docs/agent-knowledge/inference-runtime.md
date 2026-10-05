# Inference runtime: llama.cpp binaries, launch and tool calling

Scope: llama.cpp binary acquisition and launch fallback, the tool-offering gates, GBNF compilation of tool schemas, capability
detection and the chat-slot seams. Read when: touching `Providers.LlamaServer`, the llama.cpp pin, tool offering or a tool
schema, chat-template capability detection, or request-body patches. Dev-host scripting: [dev-runtime](dev-runtime.md).

## llama.cpp runtime

### llama.cpp binaries

**Rule:** `LlamaCppReleasePins.PinnedTag` is the offline floor, not the recommendation; after a pin or BYO change re-run
`scripts/run-tool-grammar-smoke-local.sh`. No Linux CUDA prebuilt: use `XE_LLAMACPP_SERVER_PATH` + `XE_LLAMACPP_VARIANT` or
the managed source build. Verify assets by the Releases API `digest`. Never cache an indeterminate device probe or let it
download (`TryGetInstalledBinaryAsync` only). Helper tools resolve beside `llama-server`; a BYO tree
without `llama-fit-params` 400s every Explore. Never build that target into a shared tree
(it stales every frozen profile's fingerprint): use a disposable `cp -a` copy of `bin/`. Never enable `--ui-mcp-proxy`.
**Authority:** `LlamaFitParamsProcessRunner`, `RuntimeBundleIdentityCalculator.IsRuntimeBundleFile`.
[evidence](../agent-knowledge-evidence.md#byo-cuda-runtime-without-llama-fit-params-2026-09-05)

### The optimized GPU launch falls back once, and only a successful safe retry is remembered

**Rule:** launch defaults live only in `LlamaServerLaunchPlan`, never in supervisors or call
sites; CPU never replays a GPU profile. A GPU launch with `-fa on` plus `-ctk`/`-ctv` that fails readiness retries once
without them (llama.cpp defaults, FA auto). Only if that safe retry succeeds does `LlamaServerLaunchFallbackStore` record the
(backend, KV type) pair in the user-level `llama-launch-fallback.json`, so a broken model never poisons later launches.
Writes re-read and merge under the sibling `llama-launch-fallback.json.lock`, never a lock on the state file (the atomic
replace fails on Windows). Frozen profiles bypass the fallback; capacity estimates stay f16-conservative.
**Prevents:** one bad model disabling the optimized launch for every node process. **Authority:**
`LlamaServerLaunchFallbackStoreTests`, `LlamaServerLaunchPolicy`.

## Tool calling and inference seams

### Tool calling has FIVE independent gates, and the UI shows only one of them

| # | Gate | Meaning |
|---|---|---|
| 1 | `request.UseLocalTools` | per-message toggle |
| 2 | `enableTools` | node setting |
| 3 | `resolution.SupportsTools` | model template capability; the `TOOLS` chip |
| 4 | `AgentHome:ToolCapableModels` | operator allow-list |
| 5 | agent `AllowedToolNames` | offered ∩ allowed; Default Assistant has none |

**Rule:** walk them in order; gate 3 is capability, gate 4 permission. Gate 4 is read live via `CachedNodeSettingsStore`,
never captured at DI composition; matching is exact-case at the offer. Use a bound agent for positive tests. Note: gate 4
auto-admits; `ToolCapableModelRegistrar` unions each installed template-capable GGUF in the descriptor's casing, and the
persisted list replaces shipped defaults (wiki 08). **Prevents:** a partial offer read as a model defect. **Authority:**
`CachedNodeSettingsStore`, `ToolCapableModelRegistrar`.

### Passing all five gates is still not enough — llama.cpp must be able to COMPILE the tool schemas

**Rule:** llama-server compiles the whole tools array into one grammar whose repetition ceiling is spent ACROSS the
whole tools array; `LlamaGrammarToolSchemaCompatibility.MaxGrammarRepetitionBound` (1024) is empirical for the whole
offer, MCP schemas included. Sanitize only the llama.cpp wire, never domain constants. Prove it with
`scripts/run-tool-grammar-smoke-local.sh` on a non-reasoning tool-capable GGUF: sanitized 200, unsanitized control 400 (a
control 200: GBNF skipped or upstream moved the limit; re-measure). Residual failure:
`FailureCategory.ModelCapabilityUnsupported`. Grammar via `--verbose` (`PUT models/{modelName}/launch-args` + unload).
**Prevents:** a 400 on each tool turn. **Authority:** wiki 03 ("The grammar repetition bound").
[evidence](../agent-knowledge-evidence.md#passing-all-five-gates-is-still-not-enough--llamacpp-must-be-able-to-compile-the-tool-schemas)

### OllamaSharp wraps HTTP **400 only** into `OllamaException` — every other status is a bare `HttpRequestException`

**Rule:** on the pinned OllamaSharp 5.4.30, `EnsureSuccessStatusCodeAsync` builds an `OllamaException` only for 400; every
other failure is an `HttpRequestException` with `StatusCode` set. Catch on the status, never a message substring.
**Prevents:** a `catch (OllamaException)` filtered on "not found" that never runs for a 404. **Authority:** OllamaSharp
`OllamaApiClient.EnsureSuccessStatusCodeAsync` (decompiled at the pin).

### Unloading a model asks BOTH local runtimes, because residency is not the provider map

**Rule:** unload asks the llama-server supervisor first (graceful, per `ModelRole`, letting a turn drain; stopping the child
also applies edited launch args), then Ollama when its gate is on. An unknown model (`StatusCode` NotFound, in
`OllamaModelUnloader.UnloadAsync`) or no daemon (`StatusCode is null`, in `UnloadFromOllamaAsync`) counts as success; any
other status propagates; `Unloaded` is false only when a role reported `TimedOutStillBusy`. Never route on the provider
map: it says where a model would be served, not where it is resident. **Prevents:** an Ollama-resident model staying loaded.
**Authority:** `ModelUnloadCoordinator` (`EjectEveryRoleAsync`, `UnloadFromOllamaAsync`), `OllamaModelUnloader.UnloadAsync`,
`RunningLocalModelEndpointTests` (`UnloadModel_*`).

### The Ollama gate-off branch registers no-op `IModelCapabilityClient` and `IOllamaModelService`, so the node still boots

**Rule:** `AddNodeModelRuntimeExtensions.AddOllamaRuntime` owns both gate branches; gate-off registers
`UnavailableModelCapabilityClient` and `UnavailableOllamaModelService`, which answer as an absent daemon (empty lists, null
`StatusCode` `HttpRequestException`), and the real service lives only on the gate-on branch. Prove composition with
`ServiceProviderValidationTests` (real root, `ValidateOnBuild`); `TestServerWebAppFactory` registers a fake
`IOllamaApiClient` and proves nothing here. **Prevents:** an opted-out secondary runtime making the host unbuildable.
**Authority:** `ServiceProviderValidationTests`, `OllamaRuntimeGateStartupTests`.
[evidence](../agent-knowledge-evidence.md#the-ollama-gate-off-branch-registers-no-op-imodelcapabilityclient-and-iollamamodelservice-so-the-node-still-boots)

### The post-turn maintenance queue shares the chat's single llama-server slot, so its cost lands on the NEXT turn

**Rule:** `ConversationMaintenanceWorker` runs Distill/Compact on the chat's slot (`--parallel 1`), so the next turn's first
token waits for the job the previous turn queued. Do not read that delay as a regression, and account for it before tuning
`DistillEveryMessages`/`AutoCompactFraction`. **Prevents:** misdiagnosed latency and mis-tuned knobs. **Authority:**
`ConversationMaintenanceWorker`.
[evidence](../agent-knowledge-evidence.md#the-post-turn-maintenance-queue-shares-the-chats-single-llama-server-slot-so-its-cost-lands-on-the-next-turn)

### "Idle" by lease is not idle right after a turn: a background request holds the model for seconds

**Rule:** a rule that acts on a chat process having no inference lease (unload it, refuse around it) treats "leased" as "wait, bounded", not as "unavailable": memory extraction and the other post-turn jobs take a lease on the model the moment the user's turn ends. Live-check any such rule with a turn on model A followed at once by a turn on model B. **Prevents:** the ordinary "switch model right after a turn" being refused with "Eject one of them" because the first model was busy for a few seconds (seen live, 2026-10-05). **Authority:** `PooledRoleLaunchAdmission.AdmitChatAsync` (`BusyResidentWaitCap`), `IdleChatEvictionResult`; model-matrix follow-ups W1.

### A work session is chat turns in a loop, and it takes the node's only invocation slot

**Rule:** mechanics are in wiki 04 §5. Drive `INodeChatStreamService.SendMessageAsync`, not
`IInvocationRunner`; stop through `INodeChatStreamCancellationRegistry.TryCancel` and write terminals with
`CancellationToken.None`. Seed `ToolResultBudgetScope`/`ProviderCallCapScope` before `await foreach`; an `AsyncLocal` set
inside the send never flows back, and `UsageSnapshot` is the last round, not a turn total. A call cap ends the STEP
(`ProviderCallBudget`), not the session. Fresh DI scope per store write; checkpoint operation ids are per phase. Session
identity comes from `AgentRunConversationContext`, never a tool argument. **Prevents:** leaked slots and wrong totals.
**Authority:** `ProviderCallBudgetChatClient`, `BudgetedToolResultAIFunction`.
[evidence](../agent-knowledge-evidence.md#work-session-context-incidents)

### Capability detection is a substring scan of the chat template — know what it can and cannot see

**Rule:** graded thinking and `native_reasoning` are separate, mutually exclusive; Harmony markers (`<|channel|>analysis`,
`reasoning_effort`) never enter the graded branch or get `think` kwargs. The tools detector matches the bare word `tools`,
even in comments, so the `TOOLS` chip is weak evidence and satisfies neither gate 4 nor 5. Work-session create/repoint checks
gates 3 and 4; a gate-4 failure at step start pauses (`StepEnded`/`ToolGate`), not `Failed`, and gate 4 is never cached in a
constructor. A partial offer is normal: the small arithmetic/time tools can still work while coder, KB, sub-agent and MCP
tools are filtered. **Prevents:** unsupported kwargs and unrecoverable sessions. **Authority:** `WorkSessionToolGate`,
`WorkSessionExecutionSupervisor`; wiki 04 (work-session tool gates).

### Budget a pre-flight through the runtime's own first round, and remember what chars/4 cannot see

**Rule:** the outer `ConversationContextBudgeter` counts the system prompt as fixed overhead only when no System message in the history carries it; a pre-flight (benchmark freeze or similar) budgets through `InvocationRunner.BudgetFirstRound`, never a hand-built message list. Tool descriptions and an 18-token per-tool JSON wrapper are counted; the chat template's own tool preamble (~198 tokens on Qwen3.8) is not. **Prevents:** a 2048-token benchmark admitted at freeze and refused at runtime (prompt paid twice, descriptions uncounted: estimate 1189 vs 2052 real). **Authority:** `ConversationContextBudgeterTests` seeded-prompt cases, `InvocationRunnerTests.BudgetFirstRound_*`, `TokenEstimatorCalibrationStore.ToolDefinitionWrapperTokens`; live-findings S5, 2026-09-28.

### Measure chat-template token overhead through llama-server, never with a hand-tuned per-template constant

**Rule:** the calibration round measures the tool preamble per model through `POST /v1/messages/count_tokens` (system + messages + tools; on b10201 it equals `/tokenize` over `/apply-template`), with and without a fixed probe tool set, and subtracts what the estimator already charges for the probe. **Prevents:** a constant tuned on one template (Qwen3.8: ~198) under- or over-charging every other template (qwen2.5-0.5b: 84). **Authority:** `LlamaTokenEstimatorCalibrationService`, `TokenEstimatorCalibrationStore.ResolveToolTemplatePreamble`; open-items O1, 2026-09-28.

### A supervisor caller that must not extend a helper-loaded model's lifetime passes the Transient intent

**Rule:** a llama.cpp caller that loads a model only for a short helper call sets `ResidencyIntent = ModelResidencyIntent.Transient` on its `LocalModelSelection` (or calls the intent overloads of `EnsureRunningAsync`/`TryAcquireInferenceLease`). Every caller that passes no intent counts as Interactive and clears the transient mark for good, and only a process the Transient request spawned is ever marked. **Prevents:** a helper-loaded model holding memory for the full idle TTL and a cap slot, or, the other way, a new background caller silently giving a draft-loaded model the interactive lifetime. **Authority:** `LlamaServerProcessSupervisor` (`RunningProcess.MarkUsed`, `JoinInflightSpawn`), `LlamaServerIdleReaper.IdleTimeToLiveFor`, `SupervisorTransientResidencyTests`; wiki 03 "Eviction & reaper", 2026-09-29.

### Every acquisition exit writes the latched status snapshot, cancellation included

**Rule:** `RuntimeAcquisitionStatusRegistry` keeps the LAST write until another replaces it, and the banner hides only on Idle or Completed. Every reported acquisition must end with a write: Completed, Failed, or Idle on cancellation (never Failed). "Installed" in node settings (the installed-runtime record) and the banner are independent sources; one can say done while the other is stuck. **Prevents:** a banner frozen at "step 2 of 2, 30 %" after a request-scoped spawn cancelled its cudart download. **Authority:** `LlamaCppBinaryManager.EnsureVariantDirAsync`, `RuntimeAcquisitionProgressTests`; tester round 6, 2026-10-03.

### A default output cap must carry its marker, or it eats half the input window

**Rule:** a node-chosen `MaxOutputTokens` (the chat output cap) is set together with `InvocationAgentDefinition.DefaultOutputCapMarkerKey`; only an explicit per-request limit may reach `ChatOptions.MaxOutputTokens` without it. **Prevents:** `ProviderCallBudgetChatClient` reserving the whole cap (half the window) out of every round's input, which trims long histories at half their room and makes a 4k window refuse its own tool offer, and `ClampToGenerationRoom` halving the configured thinking budget against a limit that already holds it. **Authority:** `ProviderCallBudgetChatClient.ResolveReservedOutputTokens`, `ProviderCallBudgetChatClientTests.GetResponseAsync_ReservesAnExplicitOutputLimitButNotTheDefaultCap`; model-matrix F2, 2026-10-04.

### Count a tool schema as the chat template renders it, and measure tool cost as a prompt-token difference

**Rule:** llama-server parses the request, so the body's JSON whitespace never reaches the tokens; the templates measured render a schema single-line with `", "` / `": "` separators. Budget tools through `TokenEstimatorCalibrationStore.RenderToolSchema`, the same helper the calibration probe uses, and judge any change by `usage.prompt_tokens` with and without the tools on at least two template families. **Prevents:** counting the indented source text, which under-counted an uncalibrated Qwen turn and over-charged a calibrated one by 150 to 200 tokens at a 4,096 window. **Authority:** `TokenEstimatorCalibrationStore` (`RenderToolSchema`, `DefaultToolTemplatePreambleTokens`); model-matrix follow-ups B1, 2026-10-05 (pinned pair: 769 / 683 / 569 real tokens on Qwen3.5, Granite 4.1, LFM2.5).

## Covered elsewhere

- A second patch onto `ChatOptions.RawRepresentationFactory` must compose the first — `docs/wiki/03-local-runtime-and-providers.md`
  ("Request-body patches on the llama.cpp path"); always `OpenAICompatibleRequestBody.Chain` (assigning discards the previous
  patch; pinned by `DeferredLlamaServerStructuredOutputTests.ResponseFormatAndThinkingSwitch_BothReachWire`).

## Stale beliefs

Superseded claims; the entries above are the active rules.

| Stale belief | Current correction |
|---|---|
| Harmony reasoning must remain undetected. | It is detected as distinct `native_reasoning`, mutually exclusive with graded thinking |
| An Agent node's `responseJsonSchema` is merely obeyed, not grammar-enforced. | Grammar-enforced from generation start, proven live with a negative control and the captured GBNF. |
| A `maxLength` in a response schema never reaches llama.cpp, because the MEAI OpenAI strict-schema transform rewrote it into a `description` first. | Still true of every other provider (validate bounds downstream); on llama-server `ApplyResponseSchemaPassthrough` writes the schema onto the body, so the grammar enforces it. |
