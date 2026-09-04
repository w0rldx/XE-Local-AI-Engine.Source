# Data Model & Persistence

> Baseline: `65de769ded3eb6e7b59eabb5daf6a8d0b89531ba` · Reviewed: 2026-08-17 · Code-grounded.

The node persists chat, agent, scheduler, model-fit and identity state in local **SQLite** through Entity Framework Core, living in the `XE-Local-AI-Engine.Client.Persistence` project. There are **two** DbContexts (`NodeChatDbContext` and `NodeIdentityDbContext`), a forward-only migration history, and a **per-column AES-256-GCM AEAD** scheme that encrypts privacy-sensitive payloads (conversation titles, message content, agent instructions, golden conversations, …) before they hit disk. This page is the maintainer reference for the schema, the encryption seams, and the migration timeline.

> **Important correction to common assumptions:** there is **no SQLCipher / no full-database `PRAGMA key` encryption** in this codebase. At-rest secrecy is achieved by encrypting **individual columns** (stored as `BLOB`) via the `NodeEncryptionSaveChangesInterceptor` + `NodePayloadProtector`. Likewise, **cloud-provider credentials are NOT stored in SQLite** — they live in a separate ASP.NET Core DataProtection-encrypted file (`cloud-credentials.enc`) owned by `CloudCredentialStore` (see [Security & Privacy](12-security-and-privacy.md)).

## Project shape

```
XE-Local-AI-Engine.Client.Persistence/
├── NodeChatDbContext.cs               # main app context (chat, agents, scheduler, model-fit, mcp)
├── NodeIdentityDbContext.cs           # ASP.NET Identity + refresh tokens
├── NodeChatDbContextFactory.cs        # design-time factory (dotnet ef) — uses NullNodeSqliteKeyHolder
├── NodeIdentityDbContextFactory.cs    # design-time factory (separate __EFMigrationsHistory_Identity)
├── INodeSqliteKeyHolder.cs            # seam: supplies the 32-byte node encryption key
├── NodeEncryptionSaveChangesInterceptor.cs  # encrypts BLOB columns on SaveChanges, restores plaintext after
├── Cryptography/
│   ├── INodeAeadCipher.cs / AesGcmNodeAeadCipher.cs   # the single AES-256-GCM primitive (12B nonce, 16B tag)
│   └── NodePayloadProtector.cs        # at-rest column protector: nonce‖ciphertext‖tag layout + AAD
├── Entities/                          # POCO entities (one type per file)
├── Configurations/                    # IEntityTypeConfiguration per entity (table/column/index mapping)
├── Implementation/                    # store classes (the persistence boundary the app calls)
├── Stores/                            # store interfaces
└── Migrations/                        # 53 implementations + 2 model snapshots (+ per-migration .Designer.cs)
```

The key-holder **implementation** that actually derives the key (`NodeSqliteKeyHolder`) lives one project up in `XE-Local-AI-Engine.Client.Application/Services/Persistence/Implementation/NodeSqliteKeyHolder.cs`; the Persistence project only owns the `INodeSqliteKeyHolder` contract and a zero-key null object. This keeps the operator-secret dependency out of the schema project.

## The two DbContexts

| Context | Base type | Migrations history table | Owns |
|---|---|---|---|
| `NodeChatDbContext` | `DbContext` | `__EFMigrationsHistory` (default) | All app data: chat, agents, playbook, golden conversations, MCP, model classifications, scheduler, model-fit, adaptive memory, uploaded files, inference profiles, knowledge, images, and development-mode state |
| `NodeIdentityDbContext` | `IdentityDbContext<NodeUser>` | `__EFMigrationsHistory_Identity` | ASP.NET Identity tables for `NodeUser` (incl. the `tutorial_state` onboarding column), plus `node_refresh_tokens` |

`NodeChatDbContext` (`NodeChatDbContext.cs`) takes an `INodeSqliteKeyHolder` in its constructor and exposes the derived key via `NodeEncryptionKey`. `OnModelCreating` applies one `IEntityTypeConfiguration` per entity from `Configurations/`. The context also exposes raw-SQL crypto helpers used by the chat write path, including `EncryptConversationTitle`, `DecryptConversationTitle`, `DecryptMessageContent`, and the compaction-summary helpers; these mirror the interceptor's AAD scheme so raw-SQL writes round-trip with change-tracker writes.

`NodeIdentityDbContext` (`NodeIdentityDbContext.cs`) deliberately uses a **separate migrations-history table** (`IdentityMigrationsHistoryTable`) so identity and app schemas migrate independently even when they share one physical SQLite file. The unique filtered index on `node_refresh_tokens.user_id` `WHERE revoked_at_utc IS NULL` enforces "at most one live refresh token per user". See [API & Hubs](09-api-and-hubs.md) for how auth consumes these.

### Design-time factories

Both contexts ship `IDesignTimeDbContextFactory` implementations (`NodeChatDbContextFactory`, `NodeIdentityDbContextFactory`) so `dotnet ef migrations add` works without booting the full host. They read the connection string from `XE_NODE_SQLITE_CONNECTION_STRING` (falling back to `node-chat.design.db` / `node-identity.design.db`) and construct `NodeChatDbContext` with a `NullNodeSqliteKeyHolder` — design-time tooling never needs the real key. (The runtime DI wiring that injects the real `NodeSqliteKeyHolder` and calls `UseSqlite` lives in the Application/Client layers, not in this schema project.)

## Encryption: how at-rest secrecy actually works

There are **two distinct crypto layers** that both delegate to the same AES-GCM primitive (`AesGcmNodeAeadCipher` implementing `INodeAeadCipher`, `Cryptography/AesGcmNodeAeadCipher.cs`):

1. **At-rest column encryption** — `NodePayloadProtector` (`Cryptography/NodePayloadProtector.cs`) wraps plaintext as `nonce(12) ‖ ciphertext ‖ tag(16)` and binds **Associated Data** = `conversationId ‖ recordId ‖ columnName ‖ "v1"` (`BuildAssociatedData`). This is what the SaveChanges interceptor uses. The AAD binding means a ciphertext copied to a different row/column/conversation fails authentication on decrypt.
2. **Streaming envelope crypto** — `EnvelopeCryptoService` (`Client.Application/.../Envelope/Implementation/EnvelopeCryptoService.cs`) reuses the same `INodeAeadCipher` for the encrypted chunk/completed message envelopes exchanged with the browser/platform. It is **not** a persistence concern but shares the primitive so there is one `AesGcm` owner. (Covered in [Chat](05-chat.md).)

### The key

`NodeSqliteKeyHolder` derives a 32-byte key with **HKDF-SHA256** from the operator secret, using `info = "c0re-node-sqlite|v1|{NodeName}"` and an empty salt (`NodeSqliteKeyHolder.cs`). The operator secret is zeroed immediately after derivation (`CryptographicOperations.ZeroMemory`), and the derived key is zeroed on `Dispose`. The key holder throws at construction if `WorkerNode:NodeName` is unset. The key never leaves the node; see [Security & Privacy](12-security-and-privacy.md).

### The SaveChanges interceptor

`NodeEncryptionSaveChangesInterceptor` (`NodeEncryptionSaveChangesInterceptor.cs`, extends `SaveChangesInterceptor`) is the heart of the scheme:

- On `SavingChanges` / `SavingChangesAsync` it walks the change tracker and **encrypts** the registered plaintext properties in place (`EncryptTrackedPayloads`), remembering the originals.
- On `SavedChanges*` (and on `SaveChangesFailed*`) it **restores** the in-memory plaintext (`RestoreTrackedPayloads`) so the tracked entity instances stay usable after the round-trip and a failed save doesn't leave ciphertext in the graph.

Each entity's encrypted columns are registered explicitly with their AAD identity. From the interceptor source:

| Entity | Encrypted column(s) | AAD (conversationId, recordId, column) |
|---|---|---|
| `NodeConversation` | `title` (optional) | (ConversationId, ConversationId, `title`) |
| `NodeMessage` | `content` (required), `metadata_json` (optional) | (ConversationId, MessageId, …) |
| `NodeToolEvent` | `plaintext_args`, `plaintext_result` (both optional) | (ConversationId, ToolCallId, …) |
| `NodeSelectedFolder` | `host_path` (required) | (`Guid.Empty`, Id, `host_path`) — node-scoped |
| `AgentDefinition` | `instructions` (required), `description` (optional) | (`Guid.Empty`, Id, …) — node-scoped |
| `CanvasWorkflow` | `graph_json` (required) | (`Guid.Empty`, Id, `graph_json`) — node-scoped |
| `AgentSkill` | `description`, `body` (both required) | (`Guid.Empty`, Id, …) — node-scoped |
| `AgentSkillResource` | `content` | (SkillId, Id, name-derived column) — moving or renaming a resource fails authentication |
| `CustomTool` | `description`, secret-bearing `config_json` | (`Guid.Empty`, Id, `description` / `custom_tool_config_json`) |
| `PlaybookAction` | behavior (required), trigger condition (optional) | (`Guid.Empty`, Id, …) — node-scoped |
| `GoldenConversation` | input turns, assertion, rubric | (`Guid.Empty`, Id, …) — node-scoped |
| `McpServerRegistration` | arguments, environment, description | (`Guid.Empty`, Id, …) — node-scoped |
| `SlashCommand` | description, action configuration | (`Guid.Empty`, Id, name-derived column) |
| `McpServerApiKey` | one-way key hash | (`Guid.Empty`, singleton Id, `mcp_api_key_hash`) |
| `IntegrationExecutionEvent` | `detail_json` (optional) | (ExecutionId, Id, `integration_execution_event_detail_json`) — the owning execution fills the conversation slot, so a re-parented event fails its tag check |
| `IntegrationApiKey` | one-way key hash (required) | (`Guid.Empty`, Id, `integration_api_key_hash`) — node-scoped; many keys per node, unlike the two singleton key rows above |
| Scheduler / model-fit rows | parameters/details/event data; raw output/diagnostics | (`Guid.Empty`, row Id, entity-specific column) |
| `ConversationUploadedFile` | `original_file_name` (required) | (ConversationId, FileId, `original_file_name`) |
| `ImageJob` | prompt, negative prompt | (`Guid.Empty`, Id, image-specific column) |
| Development rows/templates | objectives, task text/criteria, artifact/event payloads, template host paths | project/row-scoped or node-scoped AAD, by entity |

Encrypted columns are mapped as `BLOB` in the entity configurations and model snapshot (e.g. `AgentDefinition.Instructions`/`Description` are `byte[]` → `BLOB`, see `NodeChatDbContextModelSnapshot.cs`). **Golden conversations** carry an encrypted payload too (the `GoldenConversation` entity), which is why eval data is privacy-clean at rest — see [Agent Mode](04-agent-mode.md) for the harvest/eval flow. Node-scoped entities use `Guid.Empty` as the conversation component of the AAD by convention. A companion read-side `NodeEncryptionMaterializationInterceptor` (`NodeEncryptionMaterializationInterceptor.cs`) decrypts the registered columns when entities are materialized from a query, mirroring the save-side interceptor's AAD.

> **Uploaded-file blobs are encrypted off the column path.** The `ConversationUploadedFile` row only encrypts the display name (`original_file_name`) through the interceptor; the bulk payloads — the raw file bytes and the cached extracted Markdown — are **too large for the column path** and live on disk under `INodeDataDirectory.Root/uploaded-files/conversations/{conversation_id}/`, AES-256-GCM-encrypted by `UploadedFileBlobProtector` (`Client.Application/Services/DocumentIngestion/UploadedFileBlobProtector.cs`). That protector lives in the application layer (the DB-column `NodePayloadProtector` is `internal` to Persistence), so it re-uses the public `AesGcmNodeAeadCipher` primitive and replicates the exact `nonce ‖ ciphertext ‖ tag` framing + AAD layout, binding each blob with a distinct column name (`file_bytes`, `file_md`) so a bytes blob can never be swapped for an extracted-text blob under the same key. See [Security & Privacy](12-security-and-privacy.md).

## Entity inventory

Entities live in `Entities/` with mapping in the matching `Configurations/*Configuration.cs`. Every set on `NodeChatDbContext` is `internal` — `grep 'DbSet<' NodeChatDbContext.cs` is the current inventory, and the table below names them by area rather than counting them. `NodeIdentityDbContext` exposes refresh tokens in addition to the Identity sets:

| Entity | Table | Area | Notes |
|---|---|---|---|
| `NodeConversation` | `conversations` | Chat ([05](05-chat.md)) | `title` and compaction summary encrypted; pin/archive/selected-path + compaction coverage columns added by later migrations |
| `NodeMessage` | messages | Chat | `content` encrypted (BLOB); `metadata_json` encrypted; lifecycle + branch/variant + `agent_definition_id` columns |
| `NodeToolEvent` | tool events | Chat | encrypted tool args/result |
| `NodeMessageFeedback` | feedback | Chat | 👍/👎 per message; carries agent attribution |
| `NodePurgedTombstone` | tombstones | Chat | records purges for the platform sync |
| `NodeSelectedFolder` | selected folders | Agent Mode ([04](04-agent-mode.md)) | encrypted `host_path`; revocation preserves historical bindings while the live-alias unique index excludes revoked rows |
| `AgentDefinition` | `agent_definitions` | Agent Mode | encrypted instructions/description; `seed_slug` unique-filtered; memory/playbook flags |
| `AgentExecutionLog` | `agent_execution_logs` | Agent Mode | **not encrypted** (content-free telemetry). Four record kinds share the table: 0 = adaptive-memory diagnostics, 1 = durable per-invocation run envelope (terminal status, usage/timing counters, correlation + trace ids), 2 = content-free approval-decision audit, 3 = integration invocation (trigger name → `model_name`, key prefix → `provider`, target agent id → `config_hash`). Every reader and aggregate must filter by `record_kind`; columns are overloaded across kinds. `error_class`/failure category is a type/enum name only — never a message or transcript text |
| `AgentSkill` | skills | Agent Mode | encrypted description + SKILL.md body |
| `AgentSkillResource` | `agent_skill_resources` | Agent Mode | encrypted imported resource content; cascade FK to `agent_skills` |
| `CustomTool` | `custom_tools` | Agent Mode / Custom Tools | `custom__*` name; encrypted model description + secret-bearing configuration; structural kind/mode/parameters/enabled/acknowledged/version |
| `CanvasWorkflow` | workflows | Open Canvas | encrypted `graph_json` (carries agent instructions) |
| `PlaybookAction` | playbook actions | Agent Mode | encrypted behavior; analysis/eval staging + `enabled_at_utc` |
| `GoldenConversation` | golden conversations | Agent Mode eval | encrypted payload; harvest provenance |
| `McpServerRegistration` | mcp servers | MCP | transport kind, registration metadata |
| `McpServerApiKey` | `mcp_server_api_keys` | Inbound MCP | singleton bearer-key hash/fingerprint state; raw key material is not readable back |
| `McpAgentRun` / `McpAgentRunLedger` | `mcp_agent_runs` / `mcp_agent_run_ledger` | Inbound MCP | durable idempotent request lifecycle + singleton quota/accounting ledger; payload columns encrypted |
| `SlashCommand` | `slash_commands` | Chat | case-insensitive command name; encrypted description + action configuration |
| `ModelClassification` | model classifications | Models | persisted `ModelKind` + override |
| `ModelProviderMap` | `model_provider_map` | Models | **not encrypted**; PK `model_name` with `NOCASE` collation |
| `ScheduledJobDefinition` / `ScheduledJobRun` / `ScheduledJobRunEvent` | scheduler tables | Scheduler ([06](06-scheduler.md)) | Quartz-adjacent app metadata |
| `ModelFitSnapshot` / `ModelFitRecommendation` / `ModelFitBenchmark` | model-fit tables | Model-Fit | box-aware GGUF fit + benchmark results (benchmark metric columns extended by `AddInferenceProfilesAndBenchmarkMetrics`) |
| `InferenceProfile` | `inference_profiles` | Inference ([03](03-local-runtime-and-providers.md)) | **not encrypted**; one live launch-profile per `(machine_key, model_name, role, backend)` natural key; frozen launch args (`-c`/`-ngl`/`-ts`/`-ot`/`-ctk`/`-ctv`) + MoE attrs + `Explored`/`Frozen`/`Stale` status (`InferenceProfileStatus`) |
| `ConversationUploadedFile` | `conversation_uploaded_files` | Chat ([05](05-chat.md)) | encrypted `original_file_name`; metadata only — bulk bytes/extracted Markdown encrypted on disk (`UploadedFileBlobProtector`); cascade FK to `conversations` |
| `KnowledgeDocument` / `KnowledgeDocumentSection` / `KnowledgeDocumentChunk` / `KnowledgeChunkVector` | knowledge-base tables | Knowledge / RAG | document hierarchy, extracted/chunk metadata, and model/version-bound vector projections |
| `ImageJob` / `GeneratedImage` / `ImageModelProfile` | image-runtime tables | Images | encrypted prompts; generated PNG bytes live encrypted outside SQLite while rows hold metadata/status/profile state |
| `DevelopmentProject` / `DevelopmentTask` / `DevelopmentAttempt` / `DevelopmentArtifact` / `DevelopmentEvent` | development tables | Development Mode | encrypted objective/task/artifact/event payloads plus command-profile/evidence/recovery state |
| `DevelopmentTemplate` / `DevelopmentTemplateMaterialization` | development-template tables | Development Mode | reusable template definitions and selected-folder materialization provenance; host/template paths encrypted |
| `AgentWorkSession` / `AgentWorkSessionTask` / `AgentWorkSessionFinding` / `AgentWorkSessionArtifact` / `AgentWorkSessionCheckpoint` / `AgentWorkSessionEvent` | `agent_work_sessions` + five `agent_work_session_*` tables | Work Sessions | encrypted objective, task title/detail/blocked reason, finding text/source ref, checkpoint summary/state and event detail; the session title, artifact name/media type/digest stay plaintext because they are sorted, filtered or compared. **One monotonic `last_sequence` per session** feeds every child row's `sequence`, re-stamped on task and artifact mutations so a `?sinceSeq=` list replays updates as well as inserts — it is a change watermark, never a display order. Artifact bytes live encrypted outside SQLite under `work-sessions/artifacts/{sessionId:N}/`. `conversation_id` and `agent_definition_id` are loose refs with no FK, like `NodeConversation.AgentDefinitionId` |
| `IntegrationTrigger` / `IntegrationApiKey` / `IntegrationSession` / `IntegrationExecution` / `IntegrationExecutionEvent` | `integration_triggers` / `integration_api_keys` / `integration_sessions` / `integration_executions` / `integration_execution_events` | External Integrations (ADR [0008](../adr/0008-external-integrations.md)) | **One encrypted content column in the whole family**: `integration_execution_events.detail_json`, which carries an `external.output` payload verbatim; `integration_api_keys.key_hash` is encrypted for integrity, not confidentiality, on the same terms as the two singleton key rows. Everything else is plaintext structural on purpose — `failure_category`/`failure_summary` are content-free by contract, and names are sorted and filtered on. `integration_sessions.conversation_id` and `.agent_definition_id` are loose refs with **no FK**, like `AgentWorkSession`'s, and the session row is written *before* its conversation exists (the accept transaction commits first, ADR 0008 Decision §3). `principal_id` is the ownership column on `integration_api_keys`, `integration_sessions` and `integration_executions`; `key_prefix` sits beside it on executions as **audit metadata only** — nothing is looked up by it. `integration_executions.output_bytes` counts **plaintext** UTF-8 output bytes (a ciphertext `length()` would count the AES-GCM envelope). Only `integration_sessions` is listed in `ConversationFootprintPurge.CoveredChildTables`; the executions and events beneath it are purged by subselect, exactly as the five `agent_work_session_*` tables are |
| `ChatMaintenanceState` | `chat_maintenance_state` | Persistence | **not encrypted**; PK `name`, opaque `value`. Durable key/value flags for one-shot DB maintenance. Currently holds the content-encryption backfill's `content_encryption_reclaim_pending` marker: set before the legacy rows are re-encrypted and cleared only after the post-backfill `checkpoint → VACUUM → checkpoint` residue-reclamation succeeds, so a failed/interrupted cleanup is retried on the next startup (`NodeChatContentEncryptionBackfillService`). A plain table (not `PRAGMA user_version`) so `VACUUM` preserves it. |
| `BenchmarkProject` / `BenchmarkRun` / `BenchmarkWorkItem` | `benchmark_projects` / `benchmark_runs` / `benchmark_work_items` | Benchmarks | project + run definitions with encrypted configuration/result payloads, and a durable single-consumer work queue whose `attempt = 1` CHECK constraint makes a claim un-retryable; `AddBenchmarkRunLaunchReceipts` adds the launch/environment evidence columns |
| `TrainingDatasetDefinition` / `TrainingDataset` / `TrainingDatasetSample` / `ToolMockDefinition` | `training_dataset_definitions` / `training_datasets` / `training_dataset_samples` / `tool_mock_definitions` | Training ([18](18-training.md)) | encrypted definition bodies, dataset payloads, per-sample trajectories and mock configuration. `training_datasets.definition_json` is the **pinned copy** of the definition body a generation/evaluation reads instead of the live row; it is nullable on purpose (an empty-blob `NOT NULL` default would not be decryptable) and a null pin is refused, never defaulted |
| `DatasetGenerationWorkItem` / `TrainingWorkItem` | `dataset_generation_work_items` / `training_work_items` | Training | the two single-consumer durable queues; both pin `attempt = 1` with a CHECK constraint. `TrainingWorkItem` carries a `TrainingWorkKind` discriminator (`TrainingRun` / `EvaluationRun`) so one queue serves both |
| `TrainingBaseArtifact` / `TrainingRun` / `TrainingArtifact` | `training_base_artifacts` / `training_runs` / `training_artifacts` | Training | downloaded HF base checkpoints (+ license gate document), run configuration/progress, and staged export artifacts. A run's `launch_receipt_json` is the only thing that can identify an orphaned Python trainer after a host crash — only the startup reaper clears one |
| `TrainingEvaluationRun` / `TrainingComparisonReport` | `training_evaluation_runs` / `training_comparison_reports` | Training | encrypted hold-out membership + per-sample results, and the two-sided comparison report; a comparison is refused unless both sides' membership agrees on dataset, content fingerprint and hold-out id set |
| `LocalModelProxyApiKey` | `local_model_proxy_api_keys` | Inbound model proxy | singleton bearer-credential row for the OpenAI-compatible passthrough — prefix plus a one-way SHA-256 key hash (encrypted through the interceptor); the plaintext is shown once at generation and is not recoverable |
| `ModelLaunchArguments` | `model_launch_arguments` | Models | per-model custom llama.cpp launch arguments |
| `NodeUser` *(NodeIdentity ctx)* | Identity tables | Auth | `setup_completed`, `created_at_utc`, `tutorial_state` (onboarding-tour state JSON) |
| `NodeRefreshToken` *(NodeIdentity ctx)* | `node_refresh_tokens` | Auth | hashed token, one-live-per-user filtered unique index |

Adaptive agent memory was added by migration `20260622215652_AddAdaptiveAgentMemory`: it adds memory flags/scope to conversations, agent definitions, and playbook actions, plus the `agent_execution_logs` table (later shared with durable run envelopes). It does not create a separate family of memory entity tables.

### Stores are the boundary

Application code never touches `DbSet`s directly — it calls **store** classes in `Implementation/` behind interfaces in `Stores/` (e.g. `AgentDefinitionStore`, `GoldenConversationStore`, `ModelProviderMapStore`, `ScheduledJobRunStore`, and the newer `InferenceProfileStore`/`IInferenceProfileStore` for launch profiles). The chat upload store is the one exception that lives **above** the schema project: `ConversationUploadedFileStore` (`Client.Application/Services/DocumentIngestion/`) owns both the DB row and the encrypted on-disk blobs, so it sits in the application layer rather than `Persistence/Implementation/`. Read queries use `AsNoTracking()` (e.g. `ModelProviderMapStore.GetProviderForModelAsync`) and flow `CancellationToken` to every EF async call. This is the one-way dependency the schema project enforces: callers depend on store contracts, not on EF or on entity internals (most `DbSet`s are `internal`).

## Migration timeline (forward-only)

Migrations live in `Migrations/` and upgrade the existing SQLite schemas in place. New schema should prefer additive tables/columns with safe defaults, but the history also contains data-repair SQL and removal of obsolete schema (`DropApprovedUtilityImages`). Migrations are not automatically reversed when an older binary starts, so rollback depends on a separately captured compatible data-directory backup or continued use of the newer binary. The repository does not define a backup schedule, retention period, restore guarantee, RTO, or RPO. Each timestamped migration has a `.Designer.cs`; the two contexts keep separate snapshots (`NodeChatDbContextModelSnapshot.cs`, `NodeIdentityDbContextModelSnapshot.cs`, EF product version `10.0.9`).

> A few early migrations carry **no timestamp prefix** (`InitialNodeChatSchema`, `AddNodeMessageLifecycleColumns`) — these are the original chat-schema migrations that predate the timestamped naming; they coexist with the timestamped set in the same folder.

| Migration | What it added |
|---|---|
| `InitialNodeChatSchema` | Initial chat tables (conversations, messages, tool events, tombstones) |
| `AddNodeMessageLifecycleColumns` | Message lifecycle/status columns |
| `20260525075351_InitialNodeIdentitySchema` | **NodeIdentity** context: users + refresh tokens (own history table) |
| `20260526115619_AddNodeChatOrigin` | `NodeChatOrigin` on conversations/messages |
| `20260526122101_AddNodeConversationPinArchive` | Conversation pin + archive flags |
| `20260527010918_AddNodeChatBranchVariantFeedback` | Message branch/variant tree + feedback |
| `20260528101854_AddNodeConversationSelectedPath` | Selected-branch path on conversation |
| `20260529173005_AddNodeSelectedFolders` | `NodeSelectedFolder` (encrypted host path) |
| `20260530050246_AddAgentDefinitions` | `AgentDefinition` (encrypted instructions/description) |
| `20260530080425_AddMcpServers` | `McpServerRegistration` |
| `20260531061240_AddPlaybookActions` | `PlaybookAction` |
| `20260531082914_AddPlaybookActionAnalysisColumns` | Analysis-staging columns on playbook actions |
| `20260531105623_AddPlaybookEvalAndGoldenConversations` | Eval gate + `GoldenConversation` (encrypted) |
| `20260531133736_AddPlaybookActionEnabledAtUtc` | `enabled_at_utc` (eval-passed → enabled) |
| `20260601085538_AddGoldenConversationHarvestProvenance` | Harvest provenance/source on golden conversations |
| `20260601195214_AddSchedulerTables` | Scheduler definition/run/run-event tables |
| `20260602002831_AddModelClassifications` | `ModelClassification` (persisted `ModelKind`) |
| `20260602105529_AddModelFitTables` | Model-fit snapshot/recommendation/benchmark plus the legacy utility-image allow-list later removed by `DropApprovedUtilityImages` |
| `20260602195614_AddAgentDefinitionSeedProvenance` | `seed_slug` + source for the agency seed pack |
| `20260606045854_AddAgentSkills` | `AgentSkill` (encrypted description + body) |
| `20260606151544_AddCanvasWorkflows` | `CanvasWorkflow` (encrypted graph JSON) |
| `20260608093959_AddMessageAgentDefinitionId` | Per-message agent attribution |
| `20260610165152_EncryptConversationTitle` | Migrate conversation `title` → encrypted BLOB (backfill from first message) |
| `20260617222625_AddModelProviderMap` | `model_provider_map` (NOCASE PK; unencrypted) — runtime re-arch routing |
| `20260622215652_AddAdaptiveAgentMemory` | Memory flags/scope, retention/extraction metadata, and `agent_execution_logs` |
| `20260624184036_AddTutorialState` | **NodeIdentity** context: `tutorial_state` column on `AspNetUsers` (onboarding-tour progress) |
| `20260626104651_AddConversationUploadedFiles` | `conversation_uploaded_files` (chat upload attachments; encrypted display name, cascade FK) |
| `20260626234754_AddInferenceProfilesAndBenchmarkMetrics` | `inference_profiles` table (per-machine launch profiles) + benchmark metric columns on the model-fit snapshot (`pp_tokens_per_second`, `tool_loop_ms`, `cache_hit_rate`, `vram_load_bytes`, `vram_after_bytes`, …) |
| `20260701175538_AddKnowledgeBaseTables` | Knowledge-base / RAG tables: `knowledge_documents`, `knowledge_document_sections`, `knowledge_document_chunks`, `knowledge_chunk_vectors` (encrypted document store + chunk embedding vectors) |
| `20260701191341_AddImageRuntimeTables` | Local image-runtime tables: `image_jobs`, `image_model_profiles`, `generated_images` |
| `20260710163634_AddAgentDefinitionBaseScaffoldOptOut` | `disable_base_scaffold` flag on `agent_definitions` (per-agent opt-out of the base scaffold prompt) |
| `20260711002326_AddBenchmarkProfileRevisionBinding` | Bind `model_fit_benchmarks` to an inference-profile revision: `profile_id` (+ index) plus captured launch flags (`flash_attn`, `kv_type_v`) |
| `20260713170221_RepairAndUniqueMessageSequence` | Repair duplicate/gapped message sequences (data SQL) + a **unique** index on `messages (conversation_id, sequence)` enforcing one message per ordinal per conversation |
| `20260713204544_AddChatMaintenanceState` | `chat_maintenance_state` (unencrypted key/value durable flags for one-shot DB maintenance; see the content-encryption reclamation marker below) |
| `20260714144229_AddAgentRunEnvelopeColumns` | Run-envelope columns on `agent_execution_logs` (`record_kind`, `schema_version`, `invocation_id`, `request_id`, `terminal_status`, `trace_id`, `content_chunk_count`, `reasoning_chunk_count`) — the durable per-invocation run envelope shares the table with adaptive-memory diagnostics, discriminated by `record_kind` |
| `20260714161306_AddRunEnvelopeDurabilityColumns` | Envelope durability columns (`reasoning_tokens`, `started_at_utc`, `total_tokens`) + a **unique filtered** index `ix_agent_execution_logs_envelope_message_id` on `message_id` (`WHERE record_kind = 1`), so there is at most one envelope row per assistant message |
| `20260718023348_DropApprovedUtilityImages` | Removes the obsolete container utility-image allow-list table after model recommendation moved fully in-process |
| `20260718143054_AddAgentExecutionLogProvider` | Adds provider attribution to agent execution logs |
| `20260721191435_AddDevelopmentModeFoundation` | Adds development-mode project/run/review persistence |
| `20260722192133_BindDevelopmentProjectsToSelectedFolders` | Binds development projects to trusted selected-folder records |
| `20260726192021_AddLaunchPolicyFingerprintAndBenchmarkResources` | Adds launch-policy fingerprinting and measured benchmark resource fields used to detect stale inference evidence |
| `20260726203016_AddKnowledgeVectorIdentity` | Canonical knowledge vector identity (`resolved model + transform/version + width`) on documents/vectors; all pre-existing projections are explicitly tagged `legacy:unversioned` so they remain source-preserved but stale until reindexed |
| `20260728184839_AddDevelopmentCommandProfile` | Snapshots the code-owned Development command profile on each project and binds artifacts to its digest |
| `20260728200837_AddDevelopmentAttemptCommandProfile` | Captures the effective command-profile snapshot/digest on each Development attempt |
| `20260728202003_AddDevelopmentTemplates` | Adds reusable Development templates and their materialization records |
| `20260803153806_AddMcpServerApiKey` | Adds the singleton inbound-MCP bearer credential record |
| `20260803163513_HashMcpServerApiKey` | Replaces stored inbound-MCP key material with hash/fingerprint fields |
| `20260804215531_AddConversationCompactionSummary` | Adds encrypted conversation compaction summary + covered-sequence/update metadata |
| `20260804220941_AddAgentSkillImportProvenance` | Adds skill import provenance/frontmatter and encrypted `agent_skill_resources` |
| `20260806181000_AddMcpAgentRunLedger` | Adds durable inbound-MCP agent runs plus singleton accounting/quota ledger |
| `20260806201500_AddSelectedFolderRevocation` | Adds selected-folder revocation and makes alias uniqueness apply only to live registrations |
| `20260807130219_AddSlashCommands` | Adds operator-authored slash commands with encrypted description/configuration |
| `20260807193324_AddCustomTools` | Adds the Custom Tools library with encrypted description/configuration and case-insensitive unique names |
| `20260811160811_AddLocalModelProxyApiKey` | Adds the singleton inbound model-proxy bearer credential (`local_model_proxy_api_keys`) |
| `20260811161453_AddModelLaunchArguments` | Adds per-model custom llama.cpp launch arguments |
| `20260813121930_AddKnowledgeCollectionsAndProvenance` | Adds knowledge collection namespaces plus source/page/offset/content-kind/language/symbol provenance; rebuilds `chunk_fts` with weighted source-path, heading, symbol, and content fields |
| `20260814090000_AddModelProviderMapRevision` | Adds `model_provider_map.revision`, the token the installed-model deletion compare-and-swap reads |
| `20260814091525_AddBenchmarks` | Adds benchmark projects, runs and the single-consumer benchmark work queue |
| `20260815002954_AddGenerationMetadata` | Adds `generation_metadata_json` to `agent_definitions` and `agent_skills` (AI-assisted drafting provenance) |
| `20260815005024_AddTraining` | Adds training dataset definitions, datasets, samples, tool mocks and the dataset-generation work queue |
| `20260815031430_AddTrainingRuns` | Adds base artifacts, training runs, training artifacts and the training work queue |
| `20260815034444_AddTrainedModelOrigin` | Widens the `benchmark_runs` model-origin CHECK constraint to admit a trained model |
| `20260815052532_AddTrainingEvaluation` | Adds evaluation runs and comparison reports |
| `20260815171537_AddTrainingDatasetDefinitionSnapshot` | Adds `training_datasets.definition_json`, the pinned copy of the definition body a dataset was generated from |
| `20260816174029_AddBenchmarkRunLaunchReceipts` | Adds the benchmark run's launch/environment receipt columns |
| `AddIntegrationFoundation` | Adds the five external-integration tables (`integration_triggers`, `integration_api_keys`, `integration_sessions`, `integration_executions`, `integration_execution_events`) plus `conversations.kind`, whose backfill stamps `work-session` on every conversation an `agent_work_sessions` row owns |

The table above is a timeline, not an inventory — `ls Migrations/*.cs` (excluding `.Designer.cs` and
`*ModelSnapshot.cs`) is the count that is true today, and the two contexts share the folder while
keeping **2 model snapshots**. All but two files are timestamped; `InitialNodeChatSchema` and
`AddNodeMessageLifecycleColumns` are the untimestamped originals noted above.

### Notable migration mechanics

- **`EncryptConversationTitle` (`20260610165152`)** is the one migration that changes a column from plaintext to ciphertext. `NodeChatDbContext.DecryptMessageContent` exists specifically so a backfill service can re-derive each conversation's title from the (already-encrypted) first user message after this migration. The AAD layout for `title` deliberately uses `conversationId` as **both** the conversation and record component so the column is self-consistent across raw-SQL and change-tracker writes.
- **`ModelProviderMap`** is the canonical example of an **un-encrypted** table — its configuration documents the `NOCASE` collation on the `model_name` primary key so provider routing is case-insensitive without a LINQ comparer (`ModelProviderMapConfiguration.cs`).
- **Run envelope shares `agent_execution_logs`.** Rather than a new table, the durable per-invocation run envelope (a content-free lifecycle record written when a chat invocation terminalizes) reuses `agent_execution_logs` with a `record_kind` discriminator (`0` = adaptive-memory diagnostics, `1` = envelope, `2` = approval-decision audit, `3` = integration invocation). `AddAgentRunEnvelopeColumns` adds the envelope fields and `AddRunEnvelopeDurabilityColumns` adds usage/timing columns plus the `record_kind = 1`-filtered unique index on `message_id`. The whole row is plaintext structural telemetry (never encrypted, no message content), and it is covered by the conversation footprint purge (see [Security & Privacy](12-security-and-privacy.md)). The read-only `GET agents/run-envelopes` endpoint projects kind-1 rows (see [API & Hubs](09-api-and-hubs.md)). **Every read and aggregate must filter by `record_kind`** because column meanings are overloaded across the four producers. **Durability guarantee:** the envelope is written **atomically inside the terminalize transaction** — the same SQLite transaction that commits the terminal message row — so the two commit or roll back together; and the startup restart-recovery reconcile backfills an envelope for any terminal assistant row lacking one across all four terminal states (completed, failed, cancelled, interrupted), keyed on `message_id` so it can never duplicate.

## The persistence test project

`XE-Local-AI-Engine.Client.Persistence.Tests` exercises the schema and crypto against a **real on-disk SQLite file** (round-tripping through a fresh context), not an in-memory provider — so encryption, collation and migrations are genuinely tested:

- `PersistenceEncryptionTests` — verifies HKDF key derivation (`NodeSqliteKeyHolder_WhenConfigured_DerivesExpectedHkdfKey`), disposal zeroing, the helpful startup error when the secret is missing, AES-GCM encrypt→decrypt round-trips, and **negative** cases (`Decrypt_WhenTagTampered_Throws`, `Decrypt_WhenAssociatedDataMismatched_Throws`) that prove AAD binding works.
- `ModelProviderMapStoreTests` — round-trips upserts through a **new context** and asserts the `NOCASE` collation resolves differently-cased names to the same row.
- Store/migration tests (e.g. `GoldenConversationStoreTests`, `AdaptiveAgentMemoryStoreTests`, `NodeChatBranchVariantFeedbackMigrationTests`, `FeedbackInsightsStoreTests`) create a temp DB via the test context factory, run `EnsureCreated`/migrations, and assert store behavior.

Tests use `NullNodeSqliteKeyHolder` (a fixed zero key) plus a non-encrypting migration factory for tables that hold no encrypted columns. See [Testing & Validation](13-testing-and-validation.md) for the wider suite.

## Seams & invariants a maintainer must respect

- **Add an encrypted column?** You must register it in `NodeEncryptionSaveChangesInterceptor.EncryptTrackedPayloads` with a stable `(conversationId, recordId, columnName)` AAD, map the property as `byte[]`/`BLOB` in its configuration, and add a negative AAD test. Forgetting the interceptor registration silently stores plaintext.
- **AAD is part of the on-disk format.** Changing the AAD layout or the `"v1"` schema-version string makes existing ciphertext undecryptable. Bump deliberately and provide a backfill (as `EncryptConversationTitle` did).
- **Migrations are forward-only.** Prefer new nullable/defaulted columns. Any destructive cleanup needs explicit release notes, migration tests, and a rollback caveat because older binaries may not understand the upgraded database.
- **Two history tables.** When adding identity schema changes, target `NodeIdentityDbContext` (writes to `__EFMigrationsHistory_Identity`), not the chat context.
- **Cloud creds & operator secrets are not in this DB.** Don't add a "credentials" table — credentials live in DataProtection files and the WorkerHub layer; see [Security & Privacy](12-security-and-privacy.md).
- **Go through stores.** Don't expose `DbSet`s or return entities across the transport boundary; map to records/DTOs in the store layer.

## Node-run cost telemetry: twelve plaintext columns, and why they are not encrypted

`dev_workflow_node_runs` carries twelve nullable columns recording what one node-run **attempt** cost: `input_tokens`, `output_tokens`, `reasoning_tokens`, `estimated_input_tokens`, `provider_calls`, `tool_calls`, `tool_schema_tokens`, `tool_names_json`, `agent_turn_ms`, `served_model_name`, `route_json` and `work_session_steps` (migration `AddDevWorkflowNodeRunTelemetry`). They are written at ONE place — the publishing store decorator, on a terminal, `Blocked` or `WaitingForApproval` transition — so a call site added later is covered without anyone remembering to. A collection that throws or overruns its deadline leaves nulls and the transition proceeds unchanged.

They are deliberately **outside** the encryption interceptor's tracked set, and that is a policy statement rather than an oversight: the interceptor only accepts `byte[]` properties, so a non-`byte[]` column is structurally unreachable by it, and `DevWorkflowEncryptionTests` asserts the three text columns reach the database file as plaintext. What they hold is metadata only — counts, durations, structural node keys, a served model name, and outbound tool NAMES bounded at sixteen per attempt and 128 characters each, recorded only when the name matched a tool the request offered. No prompt, no tool argument, no tool result and no transcript may ever be added here; see [the trajectory data policy](../security/agent-trajectory-data-policy.md).

`agent_turn_ms` is whole-turn time — each envelope's duration spans the provider rounds and the tool loop between them — so `run_ms - agent_turn_ms` is time outside the turns, never tool time.

Two reading traps a query must respect. The columns hold the **last attempt only** — a `Pending` re-attempt clears them, and the failing attempt's nine additive numbers are merged into that reset's `node.retry.scheduled` event detail instead — so a node's true total is `row + retry snapshots`. And a null is "nobody reported it", never zero: a structural node, a row from before the migration, and a collection that could not run all read the same way. The [cost telemetry runbook](../runbooks/agent-unit-cost-telemetry-runbook.md) carries the full recipe, including the reasons every number is a lower bound.

## Related pages

- [Architecture Overview](01-architecture-overview.md)
- [Project Layout](02-project-layout.md)
- [Chat](05-chat.md) — conversation/message persistence + streaming envelope crypto
- [Agent Mode](04-agent-mode.md) — agent definitions, playbook, golden conversations, and adaptive-memory state/logging
- [Scheduler](06-scheduler.md) — scheduler tables
- [Model Fit](07-model-fit.md) — model-fit snapshot/recommendation/benchmark tables
- [Training](18-training.md) — the training/dataset/evaluation tables and their encryption
- [API & Hubs](09-api-and-hubs.md) — auth/identity consumers
- [Security & Privacy](12-security-and-privacy.md) — key derivation, AAD binding, cloud-credential storage
- [Testing & Validation](13-testing-and-validation.md)
