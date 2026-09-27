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
