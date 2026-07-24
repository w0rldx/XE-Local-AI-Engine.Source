# Data Model & Persistence

> Last reviewed: 2026-07-24 · Code-grounded.

The node persists chat, agent, scheduler, model-fit and identity state in local **SQLite** through Entity Framework Core, living in the `XE-Local-AI-Engine.Client.Persistence` project. There are **two** DbContexts (`NodeChatDbContext` and `NodeIdentityDbContext`), a forward-only migration history, and a **per-column AES-256-GCM AEAD** scheme that encrypts privacy-sensitive payloads (conversation titles, message content, agent instructions, golden conversations, …) before they hit disk. This page is the maintainer reference for the schema, the encryption seams, and the migration timeline.

> **Important correction to common assumptions:** there is **no SQLCipher / no full-database `PRAGMA key` encryption** in this codebase — `grep` for `PRAGMA`/`SQLCipher` returns nothing. At-rest secrecy is achieved by encrypting **individual columns** (stored as `BLOB`) via the `NodeEncryptionSaveChangesInterceptor` + `NodePayloadProtector`. Likewise, **cloud-provider credentials are NOT stored in SQLite** — they live in a separate ASP.NET Core DataProtection-encrypted file (`cloud-credentials.enc`) owned by `CloudCredentialStore` (see [Security & Privacy](12-security-and-privacy.md)).

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
└── Migrations/                        # 40 migrations + 2 model snapshots (+ per-migration .Designer.cs)
```

The key-holder **implementation** that actually derives the key (`NodeSqliteKeyHolder`) lives one project up in `XE-Local-AI-Engine.Client.Application/Services/Persistence/Implementation/NodeSqliteKeyHolder.cs`; the Persistence project only owns the `INodeSqliteKeyHolder` contract and a zero-key null object. This keeps the operator-secret dependency out of the schema project.

## The two DbContexts

| Context | Base type | Migrations history table | Owns |
|---|---|---|---|
| `NodeChatDbContext` | `DbContext` | `__EFMigrationsHistory` (default) | All app data: chat, agents, playbook, golden conversations, MCP, model classifications, scheduler, model-fit, adaptive memory, uploaded files, inference profiles, knowledge, images, and development-mode state |
| `NodeIdentityDbContext` | `IdentityDbContext<NodeUser>` | `__EFMigrationsHistory_Identity` | ASP.NET Identity tables for `NodeUser` (incl. the `tutorial_state` onboarding column), plus `node_refresh_tokens` |

`NodeChatDbContext` (`NodeChatDbContext.cs:12`) takes an `INodeSqliteKeyHolder` in its constructor and exposes the derived key via `NodeEncryptionKey` (`:65`). `OnModelCreating` (`:109`) applies one `IEntityTypeConfiguration` per entity from `Configurations/`. The context also exposes raw-SQL crypto helpers used by the chat write path: `EncryptConversationTitle` / `DecryptConversationTitle` / `DecryptMessageContent` (`:72`–`:107`) — these mirror the interceptor's AAD exactly so titles written via raw SQL round-trip with titles written via the change tracker.

`NodeIdentityDbContext` (`NodeIdentityDbContext.cs:11`) deliberately uses a **separate migrations-history table** (`IdentityMigrationsHistoryTable`, `:13`) so identity and app schemas migrate independently even when they share one physical SQLite file. The unique filtered index on `node_refresh_tokens.user_id` `WHERE revoked_at_utc IS NULL` (`:75`) enforces "at most one live refresh token per user". See [API & Hubs](09-api-and-hubs.md) for how auth consumes these.

### Design-time factories

Both contexts ship `IDesignTimeDbContextFactory` implementations (`NodeChatDbContextFactory`, `NodeIdentityDbContextFactory`) so `dotnet ef migrations add` works without booting the full host. They read the connection string from `XE_NODE_SQLITE_CONNECTION_STRING` (falling back to `node-chat.design.db` / `node-identity.design.db`) and construct `NodeChatDbContext` with a `NullNodeSqliteKeyHolder` — design-time tooling never needs the real key. (The runtime DI wiring that injects the real `NodeSqliteKeyHolder` and calls `UseSqlite` lives in the Application/Client layers, not in this schema project.)

## Encryption: how at-rest secrecy actually works

There are **two distinct crypto layers** that both delegate to the same AES-GCM primitive (`AesGcmNodeAeadCipher` implementing `INodeAeadCipher`, `Cryptography/AesGcmNodeAeadCipher.cs:10`):

1. **At-rest column encryption** — `NodePayloadProtector` (`Cryptography/NodePayloadProtector.cs`) wraps plaintext as `nonce(12) ‖ ciphertext ‖ tag(16)` and binds **Associated Data** = `conversationId ‖ recordId ‖ columnName ‖ "v1"` (`BuildAssociatedData`, `:56`). This is what the SaveChanges interceptor uses. The AAD binding means a ciphertext copied to a different row/column/conversation fails authentication on decrypt.
2. **Streaming envelope crypto** — `EnvelopeCryptoService` (`Client.Application/.../Envelope/Implementation/EnvelopeCryptoService.cs`) reuses the same `INodeAeadCipher` for the encrypted chunk/completed message envelopes exchanged with the browser/platform. It is **not** a persistence concern but shares the primitive so there is one `AesGcm` owner. (Covered in [Chat](05-chat.md).)

### The key

`NodeSqliteKeyHolder` derives a 32-byte key with **HKDF-SHA256** from the operator secret, using `info = "c0re-node-sqlite|v1|{NodeName}"` and an empty salt (`NodeSqliteKeyHolder.cs:62`). The operator secret is zeroed immediately after derivation (`CryptographicOperations.ZeroMemory`), and the derived key is zeroed on `Dispose`. The key holder throws at construction if `WorkerNode:NodeName` is unset. The key never leaves the node; see [Security & Privacy](12-security-and-privacy.md).

### The SaveChanges interceptor

`NodeEncryptionSaveChangesInterceptor` (`NodeEncryptionSaveChangesInterceptor.cs:13`, extends `SaveChangesInterceptor`) is the heart of the scheme:

- On `SavingChanges` / `SavingChangesAsync` it walks the change tracker and **encrypts** the registered plaintext properties in place (`EncryptTrackedPayloads`, `:59`), remembering the originals.
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
| `PlaybookAction` | behavior (required), trigger condition (optional) | (`Guid.Empty`, Id, …) — node-scoped |
| `ConversationUploadedFile` | `original_file_name` (required) | (ConversationId, FileId, `original_file_name`) |

Encrypted columns are mapped as `BLOB` in the entity configurations and model snapshot (e.g. `AgentDefinition.Instructions`/`Description` are `byte[]` → `BLOB`, see `NodeChatDbContextModelSnapshot.cs:49`,`:53`). **Golden conversations** carry an encrypted payload too (the `GoldenConversation` entity), which is why eval data is privacy-clean at rest — see [Agent Mode](04-agent-mode.md) for the harvest/eval flow. Node-scoped entities use `Guid.Empty` as the conversation component of the AAD by convention. A companion read-side `NodeEncryptionMaterializationInterceptor` (`NodeEncryptionMaterializationInterceptor.cs`) decrypts the registered columns when entities are materialized from a query, mirroring the save-side interceptor's AAD.

> **Uploaded-file blobs are encrypted off the column path.** The `ConversationUploadedFile` row only encrypts the display name (`original_file_name`) through the interceptor; the bulk payloads — the raw file bytes and the cached extracted Markdown — are **too large for the column path** and live on disk under `INodeDataDirectory.Root/uploaded-files/conversations/{conversation_id}/`, AES-256-GCM-encrypted by `UploadedFileBlobProtector` (`Client.Application/Services/DocumentIngestion/UploadedFileBlobProtector.cs`). That protector lives in the application layer (the DB-column `NodePayloadProtector` is `internal` to Persistence), so it re-uses the public `AesGcmNodeAeadCipher` primitive and replicates the exact `nonce ‖ ciphertext ‖ tag` framing + AAD layout, binding each blob with a distinct column name (`file_bytes`, `file_md`) so a bytes blob can never be swapped for an extracted-text blob under the same key. See [Security & Privacy](12-security-and-privacy.md).

## Entity inventory

Entities live in `Entities/` (POCO, one type per file) with mapping in the matching `Configurations/*Configuration.cs`. `NodeChatDbContext` exposes these `DbSet`s (`NodeChatDbContext.cs:21`–`:63`):

| Entity | Table | Area | Notes |
|---|---|---|---|
| `NodeConversation` | `conversations` | Chat ([05](05-chat.md)) | `title` encrypted; pin/archive/selected-path columns added by later migrations |
| `NodeMessage` | messages | Chat | `content` encrypted (BLOB); `metadata_json` encrypted; lifecycle + branch/variant + `agent_definition_id` columns |
| `NodeToolEvent` | tool events | Chat | encrypted tool args/result |
| `NodeMessageFeedback` | feedback | Chat | 👍/👎 per message; carries agent attribution |
| `NodePurgedTombstone` | tombstones | Chat | records purges for the platform sync |
| `NodeSelectedFolder` | selected folders | Agent Mode ([04](04-agent-mode.md)) | encrypted `host_path` |
| `AgentDefinition` | `agent_definitions` | Agent Mode | encrypted instructions/description; `seed_slug` unique-filtered; memory/playbook flags |
| `AgentExecutionLog` | `agent_execution_logs` | Agent Mode | **not encrypted** (content-free telemetry). Dual producer via `record_kind`: 0 = adaptive-memory diagnostics, 1 = durable per-invocation run envelope (terminal status, usage/timing counters, correlation + trace ids). `error_class`/failure category is a type/enum name only — never a message or transcript text |
| `AgentSkill` | skills | Agent Mode | encrypted description + SKILL.md body |
| `CanvasWorkflow` | workflows | Open Canvas | encrypted `graph_json` (carries agent instructions) |
| `PlaybookAction` | playbook actions | Agent Mode | encrypted behavior; analysis/eval staging + `enabled_at_utc` |
| `GoldenConversation` | golden conversations | Agent Mode eval | encrypted payload; harvest provenance |
| `McpServerRegistration` | mcp servers | MCP | transport kind, registration metadata |
| `ModelClassification` | model classifications | Models | persisted `ModelKind` + override |
| `ModelProviderMap` | `model_provider_map` | Models | **not encrypted**; PK `model_name` with `NOCASE` collation |
| `ScheduledJobDefinition` / `ScheduledJobRun` / `ScheduledJobRunEvent` | scheduler tables | Scheduler ([06](06-scheduler.md)) | Quartz-adjacent app metadata |
| `ModelFitSnapshot` / `ModelFitRecommendation` / `ModelFitBenchmark` | model-fit tables | Model-Fit | box-aware GGUF fit + benchmark results (benchmark metric columns extended by `AddInferenceProfilesAndBenchmarkMetrics`) |
| `InferenceProfile` | `inference_profiles` | Inference ([03](03-local-runtime-and-providers.md)) | **not encrypted**; one live launch-profile per `(machine_key, model_name, role, backend)` natural key; frozen launch args (`-c`/`-ngl`/`-ts`/`-ot`/`-ctk`/`-ctv`) + MoE attrs + `Explored`/`Frozen`/`Stale` status (`InferenceProfileStatus`) |
| `ConversationUploadedFile` | `conversation_uploaded_files` | Chat ([05](05-chat.md)) | encrypted `original_file_name`; metadata only — bulk bytes/extracted Markdown encrypted on disk (`UploadedFileBlobProtector`); cascade FK to `conversations` |
| `ChatMaintenanceState` | `chat_maintenance_state` | Persistence | **not encrypted**; PK `name`, opaque `value`. Durable key/value flags for one-shot DB maintenance. Currently holds the content-encryption backfill's `content_encryption_reclaim_pending` marker: set before the legacy rows are re-encrypted and cleared only after the post-backfill `checkpoint → VACUUM → checkpoint` residue-reclamation succeeds, so a failed/interrupted cleanup is retried on the next startup (`NodeChatContentEncryptionBackfillService`). A plain table (not `PRAGMA user_version`) so `VACUUM` preserves it. |
| `NodeUser` *(NodeIdentity ctx)* | Identity tables | Auth | `setup_completed`, `created_at_utc`, `tutorial_state` (onboarding-tour state JSON) |
| `NodeRefreshToken` *(NodeIdentity ctx)* | `node_refresh_tokens` | Auth | hashed token, one-live-per-user filtered unique index |

The **adaptive agent memory** tables were added later (migration `20260622215652_AddAdaptiveAgentMemory`); their entities surface through the same context and the `MemoryScope` enum.

### Stores are the boundary

Application code never touches `DbSet`s directly — it calls **store** classes in `Implementation/` behind interfaces in `Stores/` (e.g. `AgentDefinitionStore`, `GoldenConversationStore`, `ModelProviderMapStore`, `ScheduledJobRunStore`, and the newer `InferenceProfileStore`/`IInferenceProfileStore` for launch profiles). The chat upload store is the one exception that lives **above** the schema project: `ConversationUploadedFileStore` (`Client.Application/Services/DocumentIngestion/`) owns both the DB row and the encrypted on-disk blobs, so it sits in the application layer rather than `Persistence/Implementation/`. Read queries use `AsNoTracking()` (e.g. `ModelProviderMapStore.GetProviderForModelAsync`, `:21`) and flow `CancellationToken` to every EF async call. This is the one-way dependency the schema project enforces: callers depend on store contracts, not on EF or on entity internals (most `DbSet`s are `internal`).

## Migration timeline (forward-only)

Migrations live in `Migrations/` and upgrade an existing encrypted database in place. New schema should prefer additive tables/columns with safe defaults, but the history also contains data-repair SQL and removal of obsolete schema (`DropApprovedUtilityImages`). Migrations are not automatically reversed when an older binary starts, so release rollback requires a pre-update data-directory backup or continued use of the newer binary. Each app migration ships a `.Designer.cs` and the two contexts keep separate snapshots (`NodeChatDbContextModelSnapshot.cs`, `NodeIdentityDbContextModelSnapshot.cs`, EF product version `10.0.9`).

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
| `20260622215652_AddAdaptiveAgentMemory` | Adaptive agent memory tables + retention/extraction |
| `20260624184036_AddTutorialState` | **NodeIdentity** context: `tutorial_state` column on `AspNetUsers` (onboarding-tour progress) |
| `20260626104651_AddConversationUploadedFiles` | `conversation_uploaded_files` (chat upload attachments; encrypted display name, cascade FK) |
| `20260626234754_AddInferenceProfilesAndBenchmarkMetrics` | `inference_profiles` table (per-machine launch profiles) + benchmark metric columns on the model-fit snapshot (`pp_tokens_per_second`, `tool_loop_ms`, `cache_hit_rate`, `vram_load_bytes`, `vram_after_bytes`, …) |
| `20260701175538_AddKnowledgeBaseTables` | Knowledge-base / RAG tables: `knowledge_documents`, `knowledge_document_sections`, `knowledge_document_chunks`, `knowledge_chunk_vectors` (encrypted document store + chunk embedding vectors) |
| `20260701191341_AddImageRuntimeTables` | Local image-runtime tables: `image_jobs`, `image_model_profiles`, `generated_images` |
| `20260710163634_AddAgentDefinitionBaseScaffoldOptOut` | `disable_base_scaffold` flag on `agent_definitions` (per-agent opt-out of the base scaffold prompt) |
| `20260711002326_AddBenchmarkProfileRevisionBinding` | Bind `model_fit_benchmarks` to an inference-profile revision: `profile_id` (+ index) plus captured launch flags (`flash_attn`, `kv_type_v`) |
| `20260713170221_RepairAndUniqueMessageSequence` | Repair duplicate/gapped message sequences (data SQL) + a **unique** index on `messages (conversation_id, sequence)` enforcing one message per ordinal per conversation |
| `20260713204544_AddChatMaintenanceState` | `chat_maintenance_state` (unencrypted key/value durable flags for one-shot DB maintenance; see the content-encryption reclamation marker below) |
| `20260714144229_AddAgentRunEnvelopeColumns` | Run-envelope columns on `agent_execution_logs` (`record_kind`, `schema_version`, `invocation_id`, `request_id`, `terminal_status`, `trace_id`, `content_chunk_count`, `reasoning_chunk_count`) — the durable per-invocation run envelope shares the table with adaptive-memory diagnostics (MED-007 / R4) |
| `20260714161306_AddRunEnvelopeDurabilityColumns` | Envelope durability columns (`reasoning_tokens`, `started_at_utc`, `total_tokens`) + a **unique filtered** index `ix_agent_execution_logs_envelope_message_id` on `message_id` (`WHERE record_kind = 1`), so there is at most one envelope row per assistant message |
| `20260718023348_DropApprovedUtilityImages` | Removes the obsolete container utility-image allow-list table after model recommendation moved fully in-process |
| `20260718143054_AddAgentExecutionLogProvider` | Adds provider attribution to agent execution logs |
| `20260721191435_AddDevelopmentModeFoundation` | Adds development-mode project/run/review persistence |
| `20260722192133_BindDevelopmentProjectsToSelectedFolders` | Binds development projects to trusted selected-folder records |

(Counted on disk: **40 migration files** — 38 timestamped plus 2 untimestamped chat-schema migrations — and **2 model snapshots** = 42 `.cs` files excluding the per-migration `.Designer.cs`.)

### Notable migration mechanics

- **`EncryptConversationTitle` (`20260610165152`)** is the one migration that changes a column from plaintext to ciphertext. `NodeChatDbContext.DecryptMessageContent` exists specifically so a backfill service can re-derive each conversation's title from the (already-encrypted) first user message after this migration. The AAD layout for `title` deliberately uses `conversationId` as **both** the conversation and record component so the column is self-consistent across raw-SQL and change-tracker writes.
- **`ModelProviderMap`** is the canonical example of an **un-encrypted** table — its configuration documents the `NOCASE` collation on the `model_name` primary key so provider routing is case-insensitive without a LINQ comparer (`ModelProviderMapConfiguration.cs:18`).
- **Run envelope shares `agent_execution_logs`.** Rather than a new table, the durable per-invocation run envelope (a content-free lifecycle record written when a chat invocation terminalizes) reuses `agent_execution_logs` with a `record_kind` discriminator (`1` = envelope, `0` = adaptive-memory diagnostics). `AddAgentRunEnvelopeColumns` adds the envelope fields and `AddRunEnvelopeDurabilityColumns` adds usage/timing columns plus the `record_kind = 1`-filtered unique index on `message_id`. The whole row is plaintext structural telemetry (never encrypted, no message content), and it is covered by the conversation footprint purge (see [Security & Privacy](12-security-and-privacy.md)). The read-only `GET agents/run-envelopes` endpoint projects these rows (see [API & Hubs](09-api-and-hubs.md)). **Durability guarantee:** the envelope is written **atomically inside the terminalize transaction** — the same SQLite transaction that commits the terminal message row — so the two commit or roll back together; and the startup restart-recovery reconcile backfills an envelope for any terminal assistant row lacking one across all four terminal states (completed, failed, cancelled, interrupted), keyed on `message_id` so it can never duplicate.

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

## Related pages

- [Architecture Overview](01-architecture-overview.md)
- [Project Layout](02-project-layout.md)
- [Chat](05-chat.md) — conversation/message persistence + streaming envelope crypto
- [Agent Mode](04-agent-mode.md) — agent definitions, playbook, golden conversations, adaptive memory tables
- [Scheduler](06-scheduler.md) — scheduler tables
- [Model Fit](07-model-fit.md) — model-fit snapshot/recommendation/benchmark tables
- [API & Hubs](09-api-and-hubs.md) — auth/identity consumers
- [Security & Privacy](12-security-and-privacy.md) — key derivation, AAD binding, cloud-credential storage
- [Testing & Validation](13-testing-and-validation.md)
