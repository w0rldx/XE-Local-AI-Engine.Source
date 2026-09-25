# Data Model & Persistence

> Reviewed: 2026-09-15 · Code-grounded.

The node persists chat, agent, scheduler, model-fit and identity state in local **SQLite** through Entity Framework Core, living in the `XE-Local-AI-Engine.Client.Persistence` project. There are **two** DbContexts (`NodeChatDbContext` and `NodeIdentityDbContext`), a forward-only migration history, and a **per-column AES-256-GCM AEAD** scheme that encrypts privacy-sensitive payloads (conversation titles, message content, agent instructions, golden conversations, …) before they hit disk. This page is the maintainer reference for the schema, the encryption seams, and the migration history.

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
└── Migrations/                        # both contexts' migrations + 2 model snapshots (+ per-migration .Designer.cs)
```

The key-holder **implementation** that actually derives the key (`NodeSqliteKeyHolder`) lives one project up in `XE-Local-AI-Engine.Client.Application/Services/Persistence/Implementation/NodeSqliteKeyHolder.cs`; the Persistence project only owns the `INodeSqliteKeyHolder` contract and a zero-key null object. This keeps the operator-secret dependency out of the schema project.

## The two DbContexts

| Context | Base type | Migrations history table | Owns |
|---|---|---|---|
| `NodeChatDbContext` | `DbContext` | `__EFMigrationsHistory` (default) | All app data: chat, agents, playbook, golden conversations, MCP, model classifications, scheduler, model-fit, adaptive memory, uploaded files, inference profiles, knowledge, images, and development-mode state |
| `NodeIdentityDbContext` | `IdentityDbContext<NodeUser>` | `__EFMigrationsHistory_Identity` | ASP.NET Identity tables for `NodeUser` (incl. the `tutorial_state` onboarding column), plus `node_refresh_tokens` |

`NodeChatDbContext` (`NodeChatDbContext.cs`) takes an `INodeSqliteKeyHolder` in its constructor and exposes the derived key via `NodeEncryptionKey`. `OnModelCreating` applies one `IEntityTypeConfiguration` per entity from `Configurations/`. The context also exposes raw-SQL crypto helpers used by the chat write path, including `EncryptConversationTitle`, `DecryptConversationTitle`, `DecryptMessageContent`, and the compaction-summary helpers; these mirror the interceptor's AAD scheme so raw-SQL writes round-trip with change-tracker writes.

`NodeIdentityDbContext` (`NodeIdentityDbContext.cs`) deliberately uses a **separate migrations-history table** (`IdentityMigrationsHistoryTable`) so identity and app schemas migrate independently even when they share one physical SQLite file. `node_refresh_tokens` holds one live refresh token per signed-in session (migration `AllowConcurrentRefreshSessions` dropped the former one-live-token-per-user unique index); `replaced_by_token_id` links a rotated token to its successor for the reuse grace ([Security & Privacy](12-security-and-privacy.md)). See [API & Hubs](09-api-and-hubs.md) for how auth consumes these.

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
| `NodeConversation` | `title` (optional), `compaction_summary` (optional), `conversation_state` (optional) | (ConversationId, ConversationId, `title` / `compaction_summary` / `conversation_state`) |
| `NodeMessage` | `content` (required), `metadata_json` (optional) | (ConversationId, MessageId, …) |
| `NodeToolEvent` | `plaintext_args`, `plaintext_result` (both optional) | (ConversationId, ToolCallId, …) |
| `NodeSelectedFolder` | `host_path` (required) | (`Guid.Empty`, Id, `host_path`) — node-scoped |
| `AgentDefinition` | `instructions` (required), `description` (optional) | (`Guid.Empty`, Id, …) — node-scoped |
| `GraphWorkflowDefinition` | `graph_json` (required) | (`Guid.Empty`, Id, `graph_workflow_definition_graph_json`) — node-scoped |
| `GraphWorkflowRun` | `graph_json` (required), `input_json`, `output_json` | (DefinitionId, Id, `graph_workflow_run_*`) — the run binds its definition |
| `GraphWorkflowNodeRun` | `input_json`, `output_json`, `error`, `decided_by` (all optional) | (RunId, Id, `graph_workflow_node_run_*`) — **four distinct AAD column names on one row**: an edge condition routes on `output_json`, so a shared name would let a database writer move an input, an error or a decider into the output column and reroute a run without forging a ciphertext |
| `GraphWorkflowRunEvent` | `detail_json` (optional) | (RunId, Id, `graph_workflow_run_event_detail_json`) |
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

> **Uploaded-file blobs are encrypted off the column path.** The `ConversationUploadedFile` row only encrypts the display name (`original_file_name`) through the interceptor; the bulk payloads — the raw file bytes and the cached extracted Markdown — are **too large for the column path** and live on disk under `INodeDataDirectory.Root/uploaded-files/conversations/{conversation_id}/`, AES-256-GCM-encrypted by `UploadedFileBlobProtector` (`Client.Application/Services/DocumentIngestion/UploadedFileBlobProtector.cs`). That protector lives in the application layer (the DB-column `NodePayloadProtector` is `internal` to Persistence), so it re-uses the public `AesGcmNodeAeadCipher` primitive and replicates the exact `nonce ‖ ciphertext ‖ tag` framing + AAD layout, binding each blob with a distinct column name (`file_bytes`, `file_md`) so a bytes blob can never be swapped for an extracted-text blob under the same key. The Markdown companion is named `extracted-<fileId>.md` so it can never share a path with a `.md` upload's `<fileId>.md` bytes blob, and every Markdown reader, the staging snapshot included, reads it only for rows whose `extraction_status` is `Extracted`. See [Security & Privacy](12-security-and-privacy.md).

### The content envelope

*Encrypted and legacy plaintext rows in one column.*

The message `content` and `metadata_json` columns are the only encrypted columns that already have **legacy plaintext rows on disk**: the raw-ADO persistence path wrote them as raw UTF-8 before content encryption shipped. Their reader therefore has to tell an encrypted blob from a legacy plaintext blob without guessing, and `NodeChatContentProtection` (`Cryptography/NodeChatContentProtection.cs`) is that reader.

It prepends a two-byte header — `0xFE` marker, `0x01` format version — to the ordinary `NodePayloadProtector` payload (`nonce ‖ ciphertext ‖ tag`). `0xFE` is never a valid UTF-8 lead byte, and both columns are always produced from a .NET string via `Encoding.UTF8.GetBytes`, so the header can never collide with the start of a legacy plaintext blob. The second byte lets the framing evolve.

Reads are **read-both**: a blob carrying the header is decrypted, a blob without it is a legacy plaintext row and is returned verbatim. A table can therefore be migrated incrementally and stays fully readable throughout. The inner ciphertext uses the identical primitive and AAD (`conversationId ‖ messageId ‖ column`) as every other encrypted column, so the header is a pure prefix: an envelope-wrapped row is byte-compatible with `NodePayloadProtector` once the header is stripped. The raw persistence path, the EF materialization interceptor and the content-encryption migration all read through this one path.

## Entity inventory

Entities live in `Entities/` with mapping in the matching `Configurations/*Configuration.cs`. Every set on `NodeChatDbContext` is `internal` — `grep 'DbSet<' NodeChatDbContext.cs` is the current inventory, and the table below names them by area rather than counting them. `NodeIdentityDbContext` exposes refresh tokens in addition to the Identity sets:

| Entity | Table | Area | Notes |
|---|---|---|---|
| `NodeConversation` | `conversations` | Chat ([05](05-chat.md)) | `title`, compaction summary and distilled conversation state (migration `AddConversationState`) all encrypted; pin/archive/selected-path + compaction/state coverage columns added by later migrations. Selecting a path or minting a regenerate variant clears the compaction summary AND the conversation state together in one statement; the state's timestamp is stamped, not nulled, so a guarded distillation write (`GuardUnchanged`) from before the clear is rejected |
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
| `GraphWorkflowDefinition` | `graph_workflow_definitions` | [Graph Workflows](21-graph-workflows.md) | encrypted `graph_json` (carries per-node agent instructions); plaintext name/description plus the denormalized `graph_hash`, `node_count` and `schema_version` the list page reads instead of decrypting every graph |
| `GraphWorkflowRun` | `graph_workflow_runs` | Graph Workflows | one execution. Encrypted pinned `graph_json`, `input_json`, `output_json`; unique `request_id` (the caller's idempotency key); **no foreign key to the definition**, so a definition may be hard-deleted while terminal runs stand with the graph they actually ran; `seq` is the run's single monotonic change watermark |
| `GraphWorkflowNodeRun` | `graph_workflow_node_runs` | Graph Workflows | one row per `(run, node key)`, unique on the pair — `attempt` increments in place and per-attempt history lives in the event log. Encrypted input/output documents, error text and decider subject; plaintext `node_key`, kind, status and decision columns |
| `GraphWorkflowRunEvent` | `graph_workflow_run_events` | Graph Workflows | append-only change log, unique on `(run_id, seq)`; encrypted `detail_json` holds small structured payloads only, never a transcript |
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
| `DevelopmentProject` / `DevelopmentTask` / `DevelopmentAttempt` / `DevelopmentArtifact` / `DevelopmentEvent` | development tables | Development Mode | encrypted objective/task/artifact/event payloads plus command-profile/evidence/recovery state. **Three event types change a task's status, not one**: `TaskTransitioned`, `ReviewFinalized` (the reviewer's verdict) and `ValidationFinalized` (the deterministic gate's). A status history reconstructed from `TaskTransitioned` alone is wrong — it shows a reworking task jumping `InProgress → Blocked` and hides every gate round in between. `DevelopmentStore.PreviousRoundFeedbackAsync`'s own doc comment is the authoritative list, and `ListEventsAsync` (what the API and the React timeline consume) already returns all three |
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
| `TranscriptionSession` | `transcription_sessions` | [Audio Transcription](24-audio-transcription.md) | encrypted `title`, `config_json` and the `error_code`/`error_message` pair; plaintext structural `status`, `source_kind`, `model_id`, `detected_language`, `duration_ms` and the two unix-ms stamps. Because the error pair is encrypted it is **not SQL-queryable** — the list surface filters on `status`. The audio is never persisted: the entity has no byte-payload member and `TranscriptionNoAudioPersistenceTests` sweeps both entities against an allow-list to keep it that way |
| `TranscriptSegment` | `transcript_segments` | Audio Transcription | one transcript row per session, append-only: encrypted `text`, plaintext `seq`/`start_ms`/`end_ms`/`channel`/`confidence`. `Seq` starts at **1**, is allocated by the caller, and the unique `ux_transcript_segments_session_seq` index on `(session_id, seq)` is the only thing preventing a double allocation. Cascade FK to `transcription_sessions`, which the node connection does enforce; the store still deletes explicitly (`ExecuteDeleteAsync` in one transaction) so a transcript is never loaded and decrypted just to be thrown away |
| `NodeUser` *(NodeIdentity ctx)* | Identity tables | Auth | `setup_completed`, `created_at_utc`, `tutorial_state` (onboarding-tour state JSON) |
| `NodeRefreshToken` *(NodeIdentity ctx)* | `node_refresh_tokens` | Auth | hashed token, one-live-per-user filtered unique index |

Adaptive agent memory was added by migration `20260622215652_AddAdaptiveAgentMemory`: it adds memory flags/scope to conversations, agent definitions, and playbook actions, plus the `agent_execution_logs` table (later shared with durable run envelopes). It does not create a separate family of memory entity tables.

### Run-envelope schema versions

`agent_execution_logs` rows of kind `ChatRunEnvelope` carry a `schema_version`, so a reader can tell envelope shapes apart as the field set grows. `AgentRunEnvelope.CurrentSchemaVersion` (`Stores/IAgentExecutionLogStore.cs`) is the single source of truth the store writer and the startup recovery backfill both stamp. Every column a version added is nullable, and reads null on older rows:

| Version | Added | Null when |
|---|---|---|
| 2 | reasoning/total tokens, `started_at_utc` lifecycle fields, the deterministic message-id upsert key | — |
| 3 | written atomically inside the terminalize transaction, with the bound agent id taken from the winning message row | — |
| 4 | `tool_schema_tokens` / `max_tool_schema_tokens` — the per-turn tool-schema token estimate | on rows written by the restart-recovery backfill, which supplies no generation detail |
| 5 | `dispatched_tier` / `authored_effort` — what reasoning effort `auto` resolved to for the turn | on every turn not authored `auto`, and on restart-recovery backfill rows |
| 6 | `model_readiness_ms` — how much of `latency_ms` was the local runtime warming rather than generating | on every turn that warmed no local runtime, and on restart-recovery backfill rows |

A **filtered UNIQUE index on `message_id`** gives each envelope a deterministic identity: exactly one row per terminalized assistant message. It is the DB-level guard behind the `WHERE NOT EXISTS` that the atomic terminalize write and the startup reconcile both use, so a retry or a crash-recovery backfill can never duplicate a row, and a crash between the message commit and the envelope write leaves a recoverable key. The filter scopes the index to run-envelope rows, leaving the memory-diagnostics rows — which may repeat a message id — unaffected. SQLite treats null message ids as distinct, so an envelope missing one never trips it.

### Agent skill provenance

`AgentSkillStore` enforces three provenance rules at the store boundary, because the row's origin decides whether the runtime fences the skill's content as untrusted and whether approval may be granted per session.

- **Provenance is promote-only.** An `Imported` row stays imported even when the caller passes the `Local` default. An operator edit that simply forgot to echo the provenance back would otherwise launder third-party content into trusted content — stripping the untrusted-content fence and re-enabling session-scoped approval for it. An input that *does* carry `Imported` applies its `SourceUri`/`ImportedAtUtc`/`ContentSha256` verbatim as one unit, including a null `ContentSha256`, which is what an AI-drafted ("generated") conversion sends, because the old archive payload hash must not survive onto rewritten content.
- **`SourceUri` is shape-checked**: the literal `upload`, the literal `generated` (AI-drafted content), or `github:owner/repo`. An uploaded or drafted skill contributes its *kind* only — the operator's filename, or the model that drafted it, must not become the one unencrypted free-text string in this table.
- **`GenerationMetadataJson` is set-if-present on update**: `null` leaves the stored provenance alone rather than clearing it, so an ordinary operator edit that did not echo the block back cannot erase the record of how the skill was drafted. `AgentDefinitionInput` carries the same rule for agent definitions.

`Origin`, `SourceUri`, `ImportedAtUtc` and `ContentSha256` are what the UI's "Imported" badge, the runtime fencing decision and re-import change detection all read.

### The integration admission transaction

`IntegrationExecutionStore.AcceptAsync` reserves admission and writes the durable accept in ONE `BEGIN IMMEDIATE` transaction: re-read the key row for revocation, count the node's active executions, count the principal's, insert the session (or bump the existing one's counters), insert the execution, insert the `execution.accepted` event, commit.

- It returns **false** when the credential was revoked between authentication and admission — nothing is written, and the caller answers the same generic 401 it uses for any other invalid credential.
- It throws **`IntegrationQueueFullException`** when either cap is full — nothing is written, and the caller answers 503 with a `Retry-After`. Both caps are method parameters rather than command fields, because they are policy numbers the caller reads from `IntegrationOptions`, not part of the row being written.
- On a continuation (`NewSession` null) the session bump is scoped to the caller's own `Active` session and throws **`IntegrationSessionUnavailableException`** when it matches no row. The caller has already pre-checked ownership and status under its per-session semaphore and answers the proper 404/409 there; this is the race-free backstop for the window between that check and this transaction, not the place a caller learns which of the three it was.

**After it returns**, the caller creates the owned `NodeConversation` at the pre-minted `NewSession.ConversationId` and writes the seed message. A failure there terminalises the execution `Failed` / `internal-failure` through the coordinator's ordinary path. Because the conversation is created after the durable rows, no orphan conversation can exist and the feature carries no orphan sweep.

### The integration execution lifecycle

`IntegrationExecutionStatus` (`Entities/IntegrationEnums.cs`) carries the legal moves of an `integration_executions` row — ruling R3-2, reproduced verbatim in ADR [0008](../adr/0008-external-integrations.md). `Running` is never re-entered and no move leaves a terminal status.

| From | To — when |
|---|---|
| `Accepted` | `Queued` — waits for the invocation lease |
| `Accepted`, `Queued` | `Running` — lease held, runner about to be called |
| `Running` | `Completed`, `Failed`, `Cancelled` — the run reported a terminal state |
| `Accepted`, `Queued` | `Cancelled` — cancelled before the run started |
| `Accepted`, `Queued` | `Failed` — rejected before the run started |

`Queued` is written **only** when an execution actually waits for the lease: `Accepted → Running` is legal, and so is `Accepted`/`Queued → Cancelled`/`Failed` without ever running. It is written down rather than left to the coordinator because cross-review found `Queued` defined, counted, swept, streamed and rendered with no visible producer.

Every move into a terminal status is made by `IIntegrationExecutionStore.TryTerminalizeAsync`, which writes the status and the matching terminal event in one transaction (ruling R5-4); `UpdateStatusAsync` makes the non-terminal moves and nothing else.

`FailureCategory` is a **closed** vocabulary of exactly ten values — `trigger-unavailable`, `cloud-model-rejected`, `capacity-rejected`, `restart`, `queue-full`, `shutdown`, `internal-failure`, plus `approval-required` (an unattended run invoked an approval-gated tool), `queue-timeout` (a still-queued execution outlived `MaxQueueAgeSeconds`) and `session-policy` (historical: rows written before ADR 0008 R6-1 withdrew the caller-managed `ToolCategory.ReadLocal` restriction; no longer produced). An eleventh value is a bug, not an extension point.

### The conversation-list index: why `archived` sorts last

The conversation list runs in two variants — `purged = 0` and `purged = 0 AND archived = 0` — both ordered by `is_pinned DESC, last_seen_utc DESC LIMIT n`. In `NodeConversationConfiguration`'s covering index, `archived` is the trailing column on purpose.

Putting it second serves the active-only query perfectly, but leaves the show-all query, which does not constrain it, with a TEMP B-TREE over every non-purged conversation. Because the list join runs a correlated last-message subquery per row, that sort costs one subquery per conversation instead of `limit` of them. Trailing, `archived` is still an index-resident filter for the active query, while both queries take the ordered reverse scan.

### Purging a conversation's footprint

`ConversationFootprintPurge` is the single source of truth for the complete DB footprint of a conversation, and both the interactive immediate-purge path and the retention sweeper delete through it, so the table set can never drift between them.

Messages, tool events and uploaded-file rows declare a cascade the node connection enforces. Most of the footprint does not: feedback, tombstones, `agent_execution_logs`, and the work-session and integration families are keyed by conversation id with **no foreign key on purpose**, so those tables go only when this list names them — otherwise their rows orphan, which is a privacy gap.

- **`agent_execution_logs`** carries plaintext conversation/message correlation ids on both the adaptive-memory diagnostics rows and the durable run envelopes. Without the delete, those correlations would survive an immediate conversation purge for the separate execution-log retention period. Deleting on `conversation_id` covers both record kinds.
- **A work session owns its conversation**, so purging the conversation takes the session and its whole subtree: the objective, plan, findings and checkpoints are all conversation-derived encrypted content. Only `agent_work_sessions` carries `conversation_id`, so its five child tables resolve through a subselect on it and must go FIRST — once the session row is gone the subselect finds nothing.
- **A graph workflow run is NOT owned by its conversation.** `graph_workflow_runs.conversation_id` is a chat binding, and the purge **unbinds** it (`UPDATE … SET conversation_id = NULL`) rather than deleting the run: the run is an audit that outlives its chat. It is listed in `UnboundChildTables`, beside `CoveredChildTables`, and the foreign key's `ON DELETE SET NULL` says the same thing a second time. See [Graph Workflows](21-graph-workflows.md) §3.6.
- **An integration session owns its conversation** on the same terms, taking the session, its executions and their events. Only `integration_sessions` carries `conversation_id`, so the two descendant tables likewise resolve through a subselect and go first. `integration_triggers` and `integration_api_keys` are node-scoped and correctly untouched.

Those session-subtree tables are deliberately absent from `CoveredChildTables`, which mirrors what its test discovers: conversation- and message-keyed tables only. A session's artifact bytes live encrypted on disk under `work-sessions/artifacts/{sessionId:N}/`; the row purge removes neither those files nor upload blobs, and the caller owns both teardown paths.

### Dev-workflow restart recovery

`IDevWorkflowStore.ReconcileNonTerminalNodeRunsAsync` is restart recovery as ONE transaction. Runs auto-resume, so no run-level status moves; only node-runs the host left `Queued` or `Running` collapse back to `Pending` so the dispatcher can re-admit them, each with one `node.interrupted` event. `WaitingForApproval` and `Blocked` are durable human-wait states and survive untouched. It is idempotent by construction: a second pass finds none of those states and returns empty.

- The caller's per-row **verdicts** — an attempt spent, a human needed — are applied IN ORDER inside the same transaction as the collapse. A recovery that commits the collapse alone is one the next boot cannot finish: those rows read as ordinary `Pending` and would be re-run with no attempt or budget accounting at all. Committing both together makes recovery all-or-nothing, so any number of crashes during startup still repairs every interrupted node-run exactly once.
- **ONLY the rows whose live state still matches their verdict are collapsed.** A stranded row with no verdict, or one whose status, attempt or work session moved since the verdict was decided, is left untouched for the caller's next pass — which is what makes the method safe against a writer the caller did not expect, such as a second process sharing the database. Repairs run under `DevWorkflowVersions.Any`: the run's version has by then moved by one event per collapsed row, and the per-row match is the check that matters.
- A non-null **`unjudged`** makes this the caller's LAST pass: the rows it could not judge are blocked for a human rather than left, decided against the live row inside this transaction and so immune to the drift that stranded them. Pass it when walking away is worse than a human wait — which it is at startup, because nothing downstream picks a stranded row up again.

`ListInterruptedNodeRunsAsync` is the read half: everything left `Queued` or `Running`, read without writing anything.

### Dev-workflow node-run transitions

`TransitionDevWorkflowNodeRunCommand` (`Stores/DevWorkflowStoreContracts.cs`) carries three members whose rules are not obvious from their names.

- **`ClearWorkSession`** releases the session the row was driving, and pairs ONLY with a `TargetStatus` of `Pending` — it belongs to a re-attempt, and a retry gets a NEW session, because resuming the one that just failed resumes its poisoned context. It is also what tells a still-attached session apart from a finished one: a node run back at `Pending` with a session still on it is one the host died under, and that session's answer still counts. Releasing it on any other target would throw away the only pointer to the transcript the row's own result came from.
- **`InputJson`** rewrites what the node run is asked to do, which only the cross-node fix loop does: a re-attempt routed to an upstream node carries the failure that sent it there.
- **`DetailJson`** replaces the event detail the move would otherwise derive from `TerminalReason`, for the one move whose evidence is not on the row afterwards — a re-attempt clears the failure fields it is re-attempting because of, so its `node.retry.scheduled` event is the only place that failure survives.

### Stores are the boundary

Application code never touches `DbSet`s directly — it calls **store** classes in `Implementation/` behind interfaces in `Stores/` (e.g. `AgentDefinitionStore`, `GoldenConversationStore`, `ModelProviderMapStore`, `ScheduledJobRunStore`, and the newer `InferenceProfileStore`/`IInferenceProfileStore` for launch profiles). The chat upload store is the one exception that lives **above** the schema project: `ConversationUploadedFileStore` (`Client.Application/Services/DocumentIngestion/`) owns both the DB row and the encrypted on-disk blobs, so it sits in the application layer rather than `Persistence/Implementation/`. Read queries use `AsNoTracking()` (e.g. `ModelProviderMapStore.GetProviderForModelAsync`) and flow `CancellationToken` to every EF async call. This is the one-way dependency the schema project enforces: callers depend on store contracts, not on EF or on entity internals (most `DbSet`s are `internal`).

That one-way dependency is enforced from both sides. `Client.Persistence` grants `InternalsVisibleTo` to its own test project and to **no production assembly**, so an internal entity, `DbSet` or cipher primitive cannot be bound from the application layer at all; `LayerDependencyTests` fails a re-added grant. Where the application layer legitimately needs one of those reads, the context exposes the read itself rather than the set — `ReadWorkSessionIdForConversationAsync` and `DecryptCanvasWorkflowGraphJson` sit beside the cipher helpers for exactly that reason.

The knowledge-base retrieval and ingestion lane is the reasoned exception to "no `DbContext` above this project", and is recorded as permanent rather than pending in `Architecture/DbContextUserAllowlist.txt`: its FTS5 `MATCH` arm and its packed-float cosine scan are raw ADO that binds a status vocabulary which is simultaneously a shipped OpenAPI schema id, and two of its transactions stay open across application-layer work (an embedding-model staleness decision, and an encrypted blob write with its atomic rename). See [Knowledge Base / RAG](15-knowledge-base.md).

## Connection pragmas

*WAL, `busy_timeout` and `synchronous`.*

`NodeSqlitePragmas` (`Sqlite/NodeSqlitePragmas.cs`) applies the node's connection pragmas right after a connection opens; `NodeSqliteOptions`, bound from the `NodeSqlite` configuration section, holds their values. Both open mechanisms on the node database route through it — EF-initiated opens (migrations, EF queries/saves, health probes) via `NodeSqliteConnectionInterceptor`, and raw-ADO opens on the EF context's `DbConnection` via the shared open-if-needed helpers — which is the path the chat persistence collaborators use, the one area allowed to hold the context outside this project (see `docs/wiki/16-code-conventions.md` and `Architecture/DbContextUserAllowlist.txt`). Applying on every physical open is idempotent and cheap, so the result is correct regardless of `Microsoft.Data.Sqlite`'s connection pooling: a pooled handle keeps its pragma state, a fresh one gets it here.

The defaults are chosen for a single-file desktop database with several concurrent in-process writers (per-conversation chat writes, KB ingestion, memory extraction, the scheduler):

- **WAL** lets readers run without blocking the single writer, which is the dominant contention pattern here (frequent reads racing occasional writes). It is a persistent, file-level property, so enabling it once covers every connection to the file, including the shared Quartz job store's — see [Scheduler](06-scheduler.md) ("Concurrency posture — the scheduler shares `node.sqlite`").
- **`busy_timeout` = 5000 ms** makes a writer that meets a held write lock wait-and-retry inside SQLite for up to five seconds instead of failing instantly with `SQLITE_BUSY`. Five seconds comfortably covers a checkpoint or a large encrypted batch write while still surfacing a genuine deadlock or stall rather than hanging a request indefinitely.
- **`synchronous` = NORMAL** is the standard WAL pairing: durable across application crashes, and on OS or power loss it can only lose transactions committed since the last checkpoint, never corrupt the database. That trade is appropriate for a local chat database and is the SQLite-recommended default under WAL.

WAL is only safely settable on a writable, private-cache, on-disk connection, so `ShouldApplyWal` skips it — rather than logging a spurious warning on every open — for the three connection shapes that cannot switch into it. An in-memory database reports its journal mode as `memory` and never `wal`. A read-only connection refuses the write with SQLite error 8. A shared-cache connection (the Aspire dev integration sets one) collides with the sibling connections other services open against the node database concurrently at startup, so the switch is refused with SQLite error 6 or 8. The desktop and packaged builds use a plain private-cache data source, so they do get WAL.

**Foreign keys are enforced.** They were long believed to be off here: the node builds a bare `Data Source=` connection string, and `SqliteConnectionStringBuilder.ForeignKeys` defaults to null, so `Microsoft.Data.Sqlite` sends no pragma of its own. But the bundled `e_sqlite3` is compiled with `DEFAULT_FOREIGN_KEYS`, so SQLite's own default is on and every declared `ON DELETE CASCADE` has always fired. Resting referential integrity on a native build's compile flag is not a decision anyone made, so the connection string now says `Foreign Keys=True` and `NodeSqlitePragmas` emits `PRAGMA foreign_keys=ON` on every open — the pragma covers connection strings this process does not build itself (the Aspire dev integration's, or an operator-supplied one), and it is emitted *before* the WAL guard so it also reaches the connection shapes that cannot switch into WAL. It is also the one pragma that never degrades: `busy_timeout`, `journal_mode` and `synchronous` log a warning and carry on when SQLite rejects them, while a failure to apply `foreign_keys` fails the connection open, so no caller ever gets an unchecked connection.

The store delete paths still issue explicit ordered child deletes. With enforcement on, that order is what makes the delete legal: the references that block a parent delete declare `Restrict`, so the children have to go first.

## Migrations and schema milestones (forward-only)

Migrations live in `Migrations/` and upgrade the existing SQLite schemas in place. New schema should prefer additive tables/columns with safe defaults, but the history also contains data-repair SQL and removal of obsolete schema (`DropApprovedUtilityImages`). Migrations are not automatically reversed when an older binary starts, so rollback depends on a separately captured compatible data-directory backup or continued use of the newer binary. The repository does not define a backup schedule, retention period, restore guarantee, RTO, or RPO. Each timestamped migration has a `.Designer.cs`; the two contexts keep separate snapshots (`NodeChatDbContextModelSnapshot.cs`, `NodeIdentityDbContextModelSnapshot.cs`, EF product version `10.0.11`).

> Two early migrations carry **no timestamp prefix** (`InitialNodeChatSchema`, `AddNodeMessageLifecycleColumns`) — the original chat-schema migrations that predate the timestamped naming; they coexist with the timestamped set in the same folder. Every other file is timestamped.

**The ordered, complete list is the folder itself** — `ls XE-Local-AI-Engine.Client.Persistence/Migrations/*.cs`, excluding `.Designer.cs` and `*ModelSnapshot.cs`. This page lists only the structurally important ones, the migrations a maintainer needs to recognise when reading the schema:

| Milestone | Why it matters |
|---|---|
| `InitialNodeChatSchema`, `AddNodeMessageLifecycleColumns` | The chat core: conversations, messages, tool events, tombstones, plus the message lifecycle/status columns |
| `20260525075351_InitialNodeIdentitySchema` | The **second** context — users and refresh tokens, with its own history table (`__EFMigrationsHistory_Identity`, `NodeIdentityDbContext.IdentityMigrationsHistoryTable`) |
| `20260530050246_AddAgentDefinitions` | The agent stack's foundation (encrypted instructions/description); `AddPlaybookActions`, `AddPlaybookEvalAndGoldenConversations`, `AddAgentSkills`, `AddAdaptiveAgentMemory` and `20260824151335_AddAgentWorkSessions` build on it |
| `20260601195214_AddSchedulerTables` | Scheduler definitions, runs and run events |
| `20260610165152_EncryptConversationTitle` | The one migration that turns a plaintext column into ciphertext, with the AAD quirk described below |
| `20260617222625_AddModelProviderMap` | The unencrypted `NOCASE` routing table the runtime re-architecture introduced; `AddModelProviderMapRevision` later adds the compare-and-swap token that installed-model deletion reads |
| `20260701175538_AddKnowledgeBaseTables` | [Knowledge Base / RAG](15-knowledge-base.md): documents, sections, chunks and chunk vectors. `AddKnowledgeVectorIdentity` then makes the embedding projection versioned (pre-existing rows are tagged `legacy:unversioned`), and `AddKnowledgeCollectionsAndProvenance` adds collections and rebuilds `chunk_fts` |
| `20260701191341_AddImageRuntimeTables` | [Image Generation](14-image-generation.md): `image_jobs`, `image_model_profiles`, `generated_images` |
| `20260714144229_AddAgentRunEnvelopeColumns` | The durable per-invocation run envelope, which **shares** `agent_execution_logs` behind a `record_kind` discriminator — see the mechanics note below before writing any query against that table |
| `20260721191435_AddDevelopmentModeFoundation` | Development Mode project/attempt/review persistence; five more follow it (`BindDevelopmentProjectsToSelectedFolders`, `AddDevelopmentCommandProfile`, `AddDevelopmentAttemptCommandProfile`, `AddDevelopmentTemplates`, `20260830160604_WidenDevelopmentTasksPerProject`) |
| `20260803153806_AddMcpServerApiKey` | The singleton inbound-MCP credential. Its security shape arrives over three more migrations: `HashMcpServerApiKey` (hash + fingerprint instead of key material), `20260822013858_AddMcpServerApiKeyScope` and `20260825150223_AddMcpServerTrustTier` |
| `20260814091525_AddBenchmarks` | [Benchmarks](20-benchmarks.md): projects, runs and the single-consumer work queue; `AddBenchmarkRunLaunchReceipts` adds the launch/environment receipt that makes a run's evidence checkable |
| `20260815005024_AddTraining` | [Training](18-training.md): dataset definitions, datasets, samples, tool mocks and the generation queue; `AddTrainingRuns` and `AddTrainingEvaluation` add runs, artifacts and evaluation |
| `20260828102539_AddDevWorkflowFoundation` | Dev Workflows (see [the divergence register](22-workflow-engines-divergence-register.md)); `AddDevWorkflowRuleSets` adds the encrypted rule-set documents |
| `20260903104044_AddIntegrationFoundation` | The five external-integration tables plus `conversations.kind`, whose backfill stamps `work-session` on every conversation an `agent_work_sessions` row owns |
| `20260904145855_AddGraphWorkflows` + `20260907085114_DropCanvasWorkflows` | The four [Graph Workflows](21-graph-workflows.md) tables, then the removal of `canvas_workflows`. **Ordering is load-bearing**: the node reads and decrypts every canvas *before* migrations run and writes the converted definitions *after*, because a migration has no node key and cannot decrypt the blob — see [Graph Workflows §9](21-graph-workflows.md#9-the-open-canvas-import) |
| `20260910230421_AddExternalApps` | [External Apps](23-external-apps.md) instances and events; `AddExternalAppBridgeToken` adds the per-instance bridge credential |
| `20260913005440_AddTranscriptionSessions` | The two [Audio Transcription](24-audio-transcription.md) tables — `transcription_sessions` and `transcript_segments` — with the unique `ux_transcript_segments_session_seq` index and a cascade FK between them |

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
- **Cloud creds & operator secrets are not in this DB.** Don't add a "credentials" table — credentials live in DataProtection files and the node-local credential stores; see [Security & Privacy](12-security-and-privacy.md).
- **Go through stores.** Don't expose `DbSet`s or return entities across the transport boundary; map to records/DTOs in the store layer.

## Node-run cost telemetry: fifteen plaintext columns, and why they are not encrypted

`dev_workflow_node_runs` carries fifteen nullable columns recording what one node-run **attempt** cost: `input_tokens`, `output_tokens`, `reasoning_tokens`, `estimated_input_tokens`, `provider_calls`, `tool_calls`, `tool_schema_tokens`, `tool_names_json`, `agent_turn_ms`, `served_model_name`, `route_json` and `work_session_steps` (migration `AddAiTrendsWave`), `model_readiness_ms` (migration `AddModelReadinessTelemetry`), and `vram_free_at_load_bytes` + `vram_admitted_bytes` (migration `AddVramAtLoadTelemetry`). They are written at ONE place — the publishing store decorator, on a terminal, `Blocked` or `WaitingForApproval` transition — so a call site added later is covered without anyone remembering to. A collection that throws or overruns its deadline leaves nulls and the transition proceeds unchanged.

They are deliberately **outside** the encryption interceptor's tracked set, and that is a policy statement rather than an oversight: the interceptor only accepts `byte[]` properties, so a non-`byte[]` column is structurally unreachable by it, and `DevWorkflowEncryptionTests` asserts the three text columns reach the database file as plaintext. What they hold is metadata only — counts, durations, structural node keys, a served model name, and outbound tool NAMES bounded at sixteen per attempt and 128 characters each, recorded only when the name matched a tool the request offered. No prompt, no tool argument, no tool result and no transcript may ever be added here; see [the trajectory data policy](../security/agent-trajectory-data-policy.md).

`agent_turn_ms` is whole-turn time — each envelope's duration spans the provider rounds and the tool loop between them — so `run_ms - agent_turn_ms` is time outside the turns, never tool time.

The two VRAM columns are the odd pair here: every other column counts what the attempt SPENT, while these two READ the box at one moment. They are the free-VRAM figure the capacity gate measured just before the most recent successful load of the serving model that carried a capacity admission, and the GPU bytes it reserved for that process — carried from the admission through the load observation to a process-lifetime record, never re-probed. An unadmitted reload clears the reading: a direct, profiling or variant-moved spawn measures nothing but replaces the process those figures described, so the record drops the entry instead of letting it go stale. Unlike every other cost column, which merges member-wise on each settle, the two are written together and only once per attempt — by the first settle that carries a reading, never rewritten by a later one — so they can never pair one load's free-VRAM figure with another load's admitted bytes; a settle with no reading leaves the pair open, and a re-attempt clears it. A **warm** run therefore reports an EARLIER load's figures, and `model_readiness_ms` is what tells the two apart: a SMALL readiness there means the warmer waited for nothing, so the load these bytes describe predates the run and the box may have looked different by the time it started, while a null readiness is unmeasured and settles nothing either way. A null on the VRAM columns themselves means nobody measured — a remote or Ollama model, a model the node never loaded itself, a host with no readable global-free figure (non-NVIDIA or CPU-only), or a row written before the columns existed. Because they are readings rather than quantities they are excluded from the retry snapshot's additive vector alongside the route, the served model and the tool names.

Two reading traps a query must respect. The columns hold the **last attempt only** — a `Pending` re-attempt clears them, and the failing attempt's ten additive numbers are merged into that reset's `node.retry.scheduled` event detail instead — so a node's true total is `row + retry snapshots`. And a null is "nobody reported it", never zero: a structural node, a row from before the migration, and a collection that could not run all read the same way. The [cost telemetry runbook](../runbooks/agent-unit-cost-telemetry-runbook.md) carries the full recipe, including the reasons every number is a lower bound.

## `node-settings.json`: the save protocol

`node-settings.json` is not in either database, but it is persisted state with the same hazards, and
`NodeSettingsAdministrationService` is the only writer of record. Two properties of the file shape everything
below: it is written **whole** (a temp sibling plus an atomic rename in `NodeSettingsStore.SaveUnlockedAsync`),
and the wire DTO is optional field by optional field, so any save that omits a field must resolve it from what is
actually stored.

**Validate on a snapshot, project onto the write-time record.** `ValidateAndSaveAsync` takes a projection, not a
merged record. Validation necessarily runs against a snapshot — the policy checks are async (they resolve models)
while the store's mutation must stay pure and synchronous under its lock — so the write re-applies the same
projection to the record the store holds at write time. Saving the validated preview instead wrote that snapshot's
value back over every field a sibling writer had changed in the window: a tool-capable-model registration, a
default-model selection.

**Rebasing is refused, not risked.** Rebasing onto a record that moved can compose two individually valid updates
into an invalid one — a patch that validated "keep model warm on" against a stored warm model, rebased onto a
sibling write that cleared that model, would persist keep-warm enabled with nothing selected. The mutation
therefore compares the write-time record with the one that was validated and declines to project on a difference;
the caller reloads, re-validates and retries up to `MaxSaveAttempts` times, after which the save is REFUSED and the
caller gets a conflict result. Nothing is ever written that was not validated against the record it landed on. The
comparison is serialize-and-compare rather than the record's own equality: `StoredNodeSettings` holds an
`IReadOnlyList<string>` and nested records, whose compiler-generated equality is by REFERENCE, so two loads of the
same stored allow-list would read as a change and burn every attempt on a difference that does not exist.

**The projection runs several times per save**, so a caller that captures a value out of it gets the LAST
invocation's value, not an accumulation: each run overwrites the captured local, and the run that produced the
persisted record is the last one. `ApplyAgenticPatchAsync` relies on exactly that to name the PREVIOUS default
model for its cache invalidation.

**LOCAL-ONLY members ride along from the stored record**, never from the caller: `MachineKey`,
`TranscriptionSelectedModelId` and `TranscriptionIdleTimeoutMinutes` are deliberately absent from the wire DTO, so
a caller that builds a `StoredNodeSettings` out of a request has no value to supply and saving its record verbatim
would erase them. For `MachineKey` that is silent data loss with a long tail: the next start mints a fresh key and
every frozen inference profile — keyed by machine key — is orphaned while still reading as frozen. The carry-over
is applied both in `SaveTrustedMergedAsync` (so the record a call VALIDATES and RETURNS carries the key, including
on rejection paths that never reach a write) and inside the store mutation, where it is taken from the *latest*
record because `IMachineKeyProvider` races every settings save on the same node.

**Node-locality of the auto-effort fast model has two enforcement points.** Point 1 is this service, on BOTH save
paths (the endpoint's merged save and the MCP patch): the runner's dispatcher may move an `auto` turn onto that
model, and the turn's data was admitted upstream against a node-local one, so a cloud id, an `ext:` id or an Ollama
name is refused before it can be stored. It fires only on a CHANGE to the value — both paths validate the merged
result, so re-validating an unchanged stored value would reject every save of every other setting once the
configured fast model is uninstalled, naming a field the operator never touched. Point 2 is the dispatcher's
per-turn re-check, which shares the predicate (`NodeLocalModelGate.IsInstalledNodeLocalLlamaModelAsync`) verbatim,
so an already-stored value that stops being node-local is refused where it would actually be used. The registry
membership test is what stops an arbitrary string — a cloud model id included — from passing two resolvers that
both default the unknown to "node-local llama.cpp".

### Reading: the cache, and the synchronous twins

`CachedNodeSettingsStore` decorates the file store with a single-entry, no-TTL `IMemoryCache` entry, so the common
read is a sub-millisecond in-memory hit. Two rules keep it coherent, and both exist because a no-TTL cache makes
any stale entry **permanent** — every reader, the reconciliation pass included, would keep seeing settings that are
no longer on disk:

- **A write only INVALIDATES; it never publishes its own value.** The decorator cannot observe the order in which
  two concurrent writes reached disk (they serialize inside the inner store, which reports no ordering), so a write
  that published its own value could overwrite the cache with a version the next write had already superseded.
  Dropping the entry is order-INSENSITIVE: whichever write clears it last, the cache ends empty and the next read
  repopulates it from the canonical store.
- **A LOAD's publication is version-guarded.** A load's disk read can straddle a concurrent write, so publishing
  its result unconditionally would reintroduce the same permanently-stale entry. Every write bumps `_writeVersion`
  under the gate the load publishes under, so a load that overlapped one declines to publish and merely costs the
  next reader a file read.

`INodeRuntimeSettings` carries a **synchronous twin** of each getter for the composition/startup path (DI factory
seeds and singleton constructors) and for request-time call sites that are structurally synchronous: they read the
stored settings synchronously rather than blocking on async file I/O during host startup, which starves the thread
pool. Prefer the async getters. A sync twin is acceptable at request time ONLY when the call site cannot be made
async without rippling through an interface — the live example is `LocalToolOfferProvider.IsToolCapable`, whose
whole offer seam is synchronous by design — and it is safe there because the read resolves through
`CachedNodeSettingsStore`, where `Load` is an `IMemoryCache.TryGetValue` hit and `SaveAsync` invalidates AND
re-primes the entry, so the file is touched only on a cold first read. What is NOT acceptable is a sync twin on a
per-TOKEN path, or capturing the result in a singleton field to avoid the read — the latter is what silently
required a node restart before an edit took effect.

Each getter resolves `stored value > appsettings seed > hardcoded default`, and `NodeRuntimeSettings` captures the
seed from the bound options/configuration at construction so first-run behaviour matches plain appsettings. Some
seeds are read from `IConfiguration` directly rather than through `IOptions<T>`, for two distinct reasons worth
keeping straight: the orchestration idle-timeout would otherwise be a **DI cycle** (`OrchestrationAgentOptions` is
itself `Configure`-d from this accessor at the composition root, so the AI.Agent factory, which cannot reference
`INodeRuntimeSettings`, still gets the stored value), while the Hugging Face and transcription seeds come from
configuration because their options are registered by modules that **not every host or test context runs**, and
this accessor is constructed in all of them. For knobs with no config section at all (the llama.cpp supervisor
cap/TTL) the seed IS the hardcoded default.

### The tool-capable model allow-list is fed, never replaced

`AgentHome:ToolCapableModels` in `node-settings.json` gates tool calling on exact membership
(`LocalToolOfferProvider.IsToolCapable`). The capability is independently known —  `GgufCapabilityDetector`
classifies it deterministically from a GGUF's embedded Jinja chat template, and the result is persisted on every
installed model as `LocalModelDescriptor.IsToolCapable` — so `IToolCapableModelRegistrar` writes detection results
INTO the allow-list rather than bypassing it. The gate is synchronous and on the per-turn offer path while
capability resolution is an async store read, and, more importantly, the allow-list is the operator-visible source
of truth: it is an editable field in Node Settings (`node-settings-tool-capable-models`) and is what the Agents
page displays. Feeding it keeps one inspectable, auditable list an operator can still curate, instead of a second
invisible capability path that silently disagrees with the UI. The gate itself is unchanged.

Registration is **additive only**: no path ever removes a name, so a model an operator added by hand — or one
served by Ollama or a cloud provider, which have no GGUF descriptor at all — keeps its entry. Detection can grant
capability here, never revoke it. `ToolCapableModelBackfillService` runs the backfill once at startup, off the
critical path, because feeding capability in only at download time would leave every model already on the node
silently tool-less. It is best-effort by design: the node must start even when the model registry or the settings
file cannot be read, so a failure is logged and swallowed, and because the work is additive and idempotent a missed
run corrects itself on the next start or the next download.

Writes into the list take the same precaution as every other save. `ToolCapableModelRegistrar.AddAsync` does a
pre-check load and returns early when nothing would change — it runs on every completed download and every startup,
and `INodeSettingsStore.UpdateAsync` persists even when the mutation returns the record unchanged, so an identical
list would churn the cache and the file for nothing — but the merge itself is recomputed from the record the store
holds AT WRITE TIME, since a list built from the stale pre-check snapshot would silently drop every field another
writer changed in between.

## Readiness: what the SQLite probe proves

`NodeSqliteHealthCheck` backs `/health/ready`. Within one bounded window it exercises three capabilities without any
persistent domain mutation, and each step exists because the one before it cannot answer for it:

1. **read** — `SELECT 1` proves the file is open and readable.
2. **schema** — a sentinel core table is present in `sqlite_master`. This guards a replaced or schema-incompatible
   database that opens and reads perfectly well but carries none of the node's own tables.
3. **write** — inside a `BEGIN IMMEDIATE` transaction, a scratch-table DDL forces an actual page write to the main
   database, then rolls back. `BEGIN IMMEDIATE` alone only takes an advisory reserved lock and never touches the file,
   so it succeeds even on a read-only database; the DDL is what fails with "attempt to write a readonly database" when
   the file is not writable, and the rollback leaves zero net mutation on one that is.

The write probe's transaction must be rolled back on *every* exit — the DDL failing, the 2 s probe timeout, the caller
cancelling — because `Microsoft.Data.Sqlite` pools native handles: "closing" the connection returns a handle SQLite
still considers mid-transaction to the pool, and the next consumer to draw it fails with "cannot start a transaction
within a transaction". The raw provider message is never interpolated into the health description, because
`/health/ready` is anonymous and would otherwise leak internal error text, filesystem paths included, to remote callers
on a proxied deployment; the structured `unwritable` reason and the exception are kept for server-side logging.

## Related pages

- [Architecture Overview](01-architecture-overview.md)
- [Project Layout](02-project-layout.md)
- [Chat](05-chat.md) — conversation/message persistence + streaming envelope crypto
- [Agent Mode](04-agent-mode.md) — agent definitions, playbook, golden conversations, and adaptive-memory state/logging
- [Scheduler](06-scheduler.md) — scheduler tables
- [Model Fit](07-model-fit.md) — model-fit snapshot/recommendation/benchmark tables
- [Training](18-training.md) — the training/dataset/evaluation tables and their encryption
- [Graph Workflows](21-graph-workflows.md) — the four graph-workflow tables and the one-shot Open Canvas import
- [API & Hubs](09-api-and-hubs.md) — auth/identity consumers
- [Security & Privacy](12-security-and-privacy.md) — key derivation, AAD binding, cloud-credential storage
- [Testing & Validation](13-testing-and-validation.md)
