# ModelRole enumeration and scope audit

**Audit point:** commit `1a809330`

**Enum:** `Providers.LlamaServer/ModelRole.cs` — `Chat`, `Embedding`, `Reranker`

**Scope:** production sites that enumerate, switch, collapse, or deliberately pin `ModelRole`, plus the tests that guard those decisions. Pure pass-through records and method parameters are listed separately because they do not choose role semantics.

## Findings

| Severity | Site | Finding |
|---|---|---|
| Defect | `Providers.LlamaServer/Options/LlamaServerExternalEndpointOptions.Resolve` | `Reranker` falls through to `ChatEndpointsByModel`. This is not a supported compatibility fallback: the options type predates the reranker (`aabf45c2` versus reranker commit `1f3fbf59`), exposes no reranker map, has no reranker test, and `LlamaServerRerankerClient` immediately posts `/v1/rerank` to the returned endpoint while a chat-role llama-server does not expose that route. An external chat mapping for the same model can therefore attach reranker traffic to the wrong service. Source fix required; not changed by this documentation-only audit. |
| Defect | `Client.React/src/features/model-fit/components/InferenceProfilePanel.tsx` | The explore selector and its comments enumerate only `chat` and `embedding`, but `ModelFitMapper.TryParseRole`, the benchmark harness, launch policy, and supervisor all support `reranker`. The UI cannot start a reranker profile even though the backend accepts it. Source fix required; not changed by this audit. |
| Documentation drift | `Client.Persistence/Entities/InferenceProfile.cs`, `Client.Persistence/Stores/IInferenceProfileStore.cs`, `Providers.LlamaServer/Implementation/LlamaServerProcessSupervisor.ProcessKey`, and `Client.React/src/features/loaded-models/models/RunningModelsModels.ts` | Comments still describe only chat/embedding. The stored integer and runtime records accept `Reranker`; behavior is not restricted by those comments. |

No evidence supports treating `LlamaServerExternalEndpointOptions.Resolve` as a deliberate compatibility fallback. The type's contract says it maps `(modelName, role)`, role processes use mutually exclusive server flags, and the reranker client requires a rerank endpoint. The current non-embedding-to-chat collapse is therefore classified as a code defect.

## Enum-complete or fail-closed decisions

| Site | Classification | Evidence |
|---|---|---|
| `ILlamaServerProcessSupervisor.EvictAllRolesAsync` default implementation | Complete | Iterates `Enum.GetValues<ModelRole>()`; future members are included automatically. |
| `LlamaServerProcessSupervisor.EvictAllRolesAsync` | Complete | Uses the same enum-derived iteration. `LlamaServerLocalModelProvider.UnloadModelAsync` delegates to this authoritative helper. |
| `LlamaServerProcessSupervisor.BuildLaunchSpec` | Complete and fail-closed | Separate chat, embedding, and reranker branches; an unknown enum value throws `ArgumentOutOfRangeException`. |
| `InferenceBenchmarkHarness.RunAsync` | Complete and fail-closed | Dispatches all three roles to role-specific benchmark paths; unknown values return a failed metric result. |
| `ModelFitMapper.TryParseRole` | Complete and fail-closed | Accepts the three wire values; unknown input returns `null`. |
| `ModelFitMapper.ToWireString` | Complete for today's enum, not future-proof | Embedding and reranker are explicit; chat is the default arm. A future enum member would serialize as chat rather than fail closed. |
| `LlamaServerLaunchPolicyOptions.ContextTokensForRole` | Complete for today's enum, not future-proof | All three roles are explicit; an unknown value receives the chat context. |
| `LaunchPolicyFingerprintProvider.CaptureAsync` | Complete and honest for unknown values | Uses `Enum.IsDefined`; a defined role contributes its enum name, while an unknown integer is retained as `unknown:<value>` instead of being aliased to a current role. |

## Deliberately scoped decisions

| Site | Scope | Why it is deliberate |
|---|---|---|
| `LlamaServerLocalModelProvider.WarmModelAsync` | Chat only | Warm-up targets the interactive path; pre-spawning auxiliary roles would consume loaded-process slots. |
| `LlamaServerLocalModelProvider.GetRuntimeInfoAsync` | Chat only | The provider-neutral runtime-info contract describes the interactive chat window. |
| `DeferredLlamaServerChatClient` | Chat only | Acquires chat leases and ensures the chat endpoint by construction. |
| `DeferredLlamaServerEmbeddingGenerator` | Embedding only | Ensures the embedding endpoint by construction. |
| `LlamaServerRerankerClient` | Reranker only | Ensures the dedicated reranker process and calls `/v1/rerank`. |
| `LlamaServerProcessSupervisor` speculative-draft branch | Chat only | Speculative decoding applies to generation, not embedding or reranking. |
| `ProcessContextAllocationResolver` tier selection | Chat versus auxiliary roles | Chat uses descending hardware-derived tiers; embedding and reranker deliberately share the bounded 2048-token auxiliary allocation. |
| `SubAgentSpawnService` | Chat only | A spawned sub-agent runs a chat/tool loop. |
| `RunSavedAgentHandler` | Chat only | A scheduled saved agent runs the chat/tool path. |
| `CapacityService.SnapshotRunningKeysAsync` Ollama branch | Chat only | Ollama's running-model snapshot has no llama-server role dimension; the only competing sub-agent workload here is chat. The llama.cpp branch preserves each health row's actual role. |
| `InferenceProfilePanel` selector | **Not deliberate; defect** | Its claim that the backend rejects reranker contradicts the current mapper and harness. See Findings. |

## Incorrect collapse

| Site | Current mapping | Verdict |
|---|---|---|
| `LlamaServerExternalEndpointOptions.Resolve` | Embedding → embedding map; chat, reranker, and unknown values → chat map | **Defect.** Reranker requires its own compatible `/v1/rerank` endpoint or an explicit no-match/fail-closed result. |

## Pass-through sites

These sites carry role identity but do not enumerate or reinterpret it:

- Provider contracts and records: `LlamaServerEndpoint`, `LlamaServerLaunchSpec`, `LlamaServerProcessHealth`, `IInferenceProfileResolver`, `ILlamaServerLaunchPolicy`, and `IProcessContextAllocationResolver`.
- Provider implementations: `LlamaServerLaunchPolicy`, `DefaultInferenceProfileResolver`, and `DefaultProcessContextAllocationResolver`.
- Application capacity/profile services: `ICapacityService`, `IModelFootprintProvider`, `ISpawnSerializer`, `CapacityService`'s llama.cpp branch, `ModelFootprintProvider`, `SpawnSerializer`, `IInferenceProfileService`, `InferenceProfileResolver`, `InferenceProfileService`, and `ModelFitRefreshService`.
- Persistence/API/UI projection: `InferenceProfile`, `IInferenceProfileStore`, `ModelFitMapper`'s record projection, and `InferenceProfilePanel`'s row display.

## Test coverage

The role-completeness guards are:

- `LlamaServerProviderContractTests.UnloadModelAsync_EvictsAllRoles`
- `LlamaServerProviderContractTests.EvictAllRolesAsync_EvictsEveryDefinedModelRole`
- supervisor spawn/launch-policy tests covering chat, embedding, and reranker launch roles
- `InferenceBenchmarkHarnessTests` for all three role dispatch paths
- `ModelFitMapperRoleTests` for all three wire values
- `LlamaServerLaunchPolicyOptionsTests.ContextTokensForRole_MapsEachRoleToItsDefault`

Deliberate-scope tests cover chat warm-up, role-specific clients, sub-agent/scheduler chat admission, allocation tiers, and role-specific profile resolution.

Three gaps remain:

1. no test covers `LlamaServerExternalEndpointOptions.Resolve` for `Reranker`;
2. no frontend test requires the inference-profile selector to expose `reranker`;
3. no test forces an undefined `ModelRole` through `BuildLaunchSpec` to pin its fail-closed guard.

The complete test-file reference inventory at the audit point is:

- capacity/scheduler: `CapacityServiceTests`, `ModelFootprintProviderTests`, `ProcessContextAllocationResolverTests`, `SpawnSerializerTests`, `SubAgentSpawnServiceTests`, and `RunSavedAgentHandlerTests`;
- profile/API: `InferenceProfileEndpointTests`, `ModelFitMapperRoleTests`, `InferenceBenchmarkHarnessTests`, `InferenceInvalidationEvaluatorTests`, `InferenceProfileResolverTests`, `InferenceProfileServiceTests`, `LaunchPolicyFingerprintProviderTests`, and `ModelFitRefreshServiceTests`;
- provider/supervisor: `GpuLoadAdmissionCrossSupervisorTests`, `LinuxProcessGroupTreeKillTests`, `LlamaCppSourceBuildServiceTests`, `LlamaFitParamsProcessRunnerTests`, `LlamaServerLaunchPolicyOptionsTests`, `LlamaServerLaunchPolicyTests`, `LlamaServerProviderContractTests`, `LlamaServerRerankerClientTests`, `OverrideSelectorAndOptionsTests`, `SourceBuildRecoveryTests`, `SupervisorAdmissionTests`, `SupervisorCrashAndSurfaceTests`, `SupervisorEvictionTests`, `SupervisorLaunchFallbackTests`, `SupervisorLaunchSpecProfileTests`, `SupervisorLifecycleTests`, `SupervisorProfilingTests`, `SupervisorRaceTests`, `SupervisorSpawnArgsTests`, `SupervisorTestDoubles`, and `SupervisorWedgedReuseTests`.

## Re-audit trigger

When adding a `ModelRole`:

1. update or deliberately reject it in every enum-complete table row above;
2. keep provider unload routed through `EvictAllRolesAsync`;
3. add a dedicated external-endpoint mapping or fail closed;
4. update wire parsing, UI selectors, persistence comments, and role-specific tests;
5. run a repository search for `ModelRole.` and `Enum.GetValues<ModelRole>()` and append any new decision site to this audit.
