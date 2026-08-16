# Chat Subsystem

> Baseline: `50cae1410b23fa1e7258d343c1f2d926c6eb41fb` · Reviewed: 2026-08-08 · Code-grounded.

The chat subsystem is the node's interactive conversation surface: a React feature (`src/features/chat`) that streams turns over a local SignalR hub into a backend pipeline (`Client.Application/Services/Chat`) which resolves a model + agent per turn, runs the Microsoft Agent Framework loop through a single re-selecting `IChatClient`, and persists every turn to SQLite with titles and content protected by per-column AEAD. This page traces a turn end-to-end: model resolution (including the "local default → installed GGUF chat model" rule), the streaming/persistence pump, ordered reasoning↔tool↔answer parts, opt-in knowledge-base grounding and source attribution, per-send sampling, per-message agent attribution, reasoning-effort clamping for cloud models, file attachments (encrypted upload → pure-.NET extraction → plain-chat inlining or agent-mode sandbox staging), browser-side voice / text-to-speech output, the client stream watchdog + provider self-heal, and the at-rest encryption of titles and content. Provider plumbing lives in [Local runtime & providers](03-local-runtime-and-providers.md); agent resolution in [Agent Mode](04-agent-mode.md); persistence/migrations in [Data & persistence](08-data-and-persistence.md); hubs/endpoints in [API & hubs](09-api-and-hubs.md).

## Scope at a glance

| Concern | Owner |
|---|---|
| Per-call cloud-vs-local routing | `RuntimeChatClient` (`Services/CloudProviders/Implementation/RuntimeChatClient.cs`) |
| Send orchestration / streaming | `NodeChatStreamService` (`Services/Chat/Implementation/NodeChatStreamService.cs`) |
| Regeneration (assistant revision) | `NodeChatRegenerationService` |
| Persistence facade | `NodeChatPersistenceService` over `NodeChatPersistenceWriter` |
| Local-default model pick | `LocalDefaultChatModelResolver` |
| Ordered parts interleave | `NodeChatPartAccumulator` |
| Tool offer per turn | `LocalToolOfferProvider` |
| Plain-chat knowledge grounding | `KnowledgeChatContextComposer` + `IKnowledgeSearchService` |
| Reasoning-effort vocabulary | `ReasoningEffortNormalizer` (backend) / `clampReasoningEffort` (React) |
| File attachments (store + extraction) | `ConversationUploadedFileStore` + `DocumentTextExtractor` (`Client.Application/Services/DocumentIngestion`) |
| Plain-chat attachment inlining | `ConversationAttachmentContextComposer` (`Services/Chat/Implementation`) |
| Agent-mode attachment staging | `IConversationSandboxStager` (see [Agent Mode](04-agent-mode.md)) |
| Browser voice / TTS tap | `useVoicePlayback` + `VoiceRuntime` (`Client.React`, see [React client](10-react-client.md)) |
| Stream watchdog | `guardNodeChatStream` (`features/chat/api/NodeChatStreamGuard.ts`) |
| Bounded live delivery / resume | `ChatStreamEventSink` + `InvocationResumeRegistry` |
| Inbound message-size cap | `SecurityOptions.MaxMessageSizeKb` + `LocalChatHub.EnsureMessageWithinSizeCap` |
| Provider self-heal on eject/restart | `DeferredLlamaServerChatClient` (`Providers.LlamaServer`) |
| At-rest encryption | `NodeChatDbContext` + `NodePayloadProtector` (`Client.Persistence`) |
| React feature | `Client.React/src/features/chat` |

## End-to-end turn flow

```
React Chat.tsx
  └─ nodeChatAdapter.sendMessage(req, signal)        (NodeChatAdapter.ts)
       └─ SignalR stream "SendMessage" (NodeChatConnection)  ── local hub ──▶
            NodeChatStreamService.SendMessageAsync
              1. mutationGuard.EnsureMutableAsync        (reject remote-origin convo)
              2. persist user message  ─▶ UserMessagePersisted
              3. ResolveTurnAsync  (active model + agent + orchestration + capabilities)
              4. CreateAssistantPlaceholder (stamped with agent id/name/effort) ─▶ AssistantPending
              5. MarkAssistantQueued ─▶ AssistantQueued
              6. RunInvocationAsync (acquire collision lease) ─▶ AssistantStreaming
                   └─ invocationRunner.RunAsync  ──▶ MAF loop on RuntimeChatClient
              7. PumpInvocationStatesAsync: delta-only frames ─▶ AssistantDelta, tool events ─▶ ToolCall*
              8. terminal ─▶ AssistantCompleted | Failed | Cancelled | Interrupted
```

`SendMessageAsync` (`NodeChatStreamService.cs`) validates non-empty content then delegates to the `IAsyncEnumerable` core. Each step `yield return`s a `ChatStreamEvent` tagged with a monotonic `sequence` (`NodeChatStreamSequence`) so the browser can order concurrently-produced events. The user-visible SignalR stream order is fixed: `UserMessagePersisted → AssistantPending → AssistantQueued → AssistantStreaming → (AssistantDelta | ToolCall*)* → terminal`.

### The collision lease and connection-independent lifecycle

A turn is **Queued** until `RunInvocationAsync` acquires the shared invocation slot via `eventDispatcher.ReportInvocationAssignedAsync` (`NodeChatStreamService.cs`); only then does it transition to **Streaming**. This keeps a turn waiting behind another in-flight invocation honestly "queued" rather than prematurely "streaming".

A critical invariant in `NodeChatStreamService.SendMessageCoreAsync` is that the **client `cancellationToken` is deliberately NOT linked directly to the run.** When the browser disconnects, the SignalR forwarding loop detaches while the run and persistence pump continue on a separate run token. `LocalChatHub.TrackAttachment` records whether any stream is watching the invocation; `DetachedInvocationReaper` cancels a still-unwatched run only after the stored `DetachedGraceSeconds` elapses (default 300 seconds, read live; `0` disables this reaper). A reconnect or reload can therefore reattach through `ResumeMessage` / `ResumeConversation`. A user Stop cancels immediately through `INodeChatStreamCancellationRegistry`; grace expiry uses `IInvocationRunner.CancelDetached`. The handlers must be unsubscribed *after* awaiting both tasks, or the terminal event can arrive after teardown and the message is falsely persisted as interrupted.

### Delta-only wire delivery and bounded queues

An `assistant-delta` carries only `delta` / `reasoningDelta` plus `contentOffset` / `reasoningOffset`; it never repeats the accumulated answer. Full text is reserved for `assistant-snapshot` (resume/gap repair) and terminal events. The browser checks both offsets before appending; a gap or overlap disposes the subscription and re-enters through `ResumeMessage`, whose opening snapshot replaces local text and resets the offsets. This makes dropped/replayed frames detectable without retaining duplicate full-message payloads in each event.

Live delivery is bounded independently of durable persistence. `ChatStreamEventSink` uses a non-blocking bounded channel with `Chat:StreamBudget.QueueCapacity` (default 2,048 events) and `MaxQueuedChars` (default 1,048,576 characters); producers never wait, so a slow/disconnected browser cannot stall the pump's database terminalization. Overflow emits one `assistant-reconcile` per burst, causing the client to resume from an authoritative snapshot. Resume subscribers are also bounded, limited to four per invocation by default, and snapshots above `MaxReplaySnapshotChars` reconcile through a persisted-conversation refetch instead of sending an oversized replacement. Delta production is coalesced before sequence assignment by `EmitDebounceMs` (40 ms by default), so coalescing cannot create sequence holes.

## Model resolution (the per-turn `RuntimeChatClient` seam)

`RuntimeChatClient` (`Services/CloudProviders/Implementation/RuntimeChatClient.cs`) is the node's single registered `IChatClient`. It is a thin wrapper that **re-selects cloud-vs-local on every call** (`ResolveActiveClient`): it asks `IActiveCloudChatClientFactory.TryCreateActiveCloudChatClient` and uses the cloud client when a cloud provider is selected and usable, otherwise the lazily-created, reused local client. Consequences a maintainer must respect:

- Singletons (the agent factories) capture this wrapper once, but signing in/out of a cloud provider (Codex) takes effect on the **next send** without restarting the node.
- The local client is stable → resolved once and reused. The cloud client is owned and lifecycle-managed by the cloud factory (which caches it on a selection fingerprint and never disposes a swapped-out wrapper mid-flight). `RuntimeChatClient.Dispose` disposes **only** the local client; disposing the resolved client at the per-call boundary would be a bug for both paths.
- When a cloud provider is selected but unusable (no Codex session), the factory throws a **typed re-auth error** that propagates to the caller as a re-authenticate prompt — it does **not** silently route local.

### Which model name drives the turn

`NodeChatStreamService.ResolveTurnAsync` computes the active model with the same precedence the model list/picker uses:

1. Explicit `request.Model` (any picked model, including an Ollama model) — honored unchanged.
2. Otherwise the operator's persisted node default (`StoredNodeSettings.DefaultModelName`).
3. Otherwise (the **"Local runtime default"** case, `request.Model` null/blank) → resolved through `ILocalDefaultChatModelResolver`.

### Local-default → installed GGUF chat model

`LocalDefaultChatModelResolver` (`Services/Chat/Implementation/LocalDefaultChatModelResolver.cs`) resolves the local default from **installed GGUF (llama.cpp) models only — never Ollama**. Rules:

- Enumerates installed models via `IGgufModelStore.ListInstalledModelsAsync`.
- Excludes entries whose **persisted effective kind** (`OverrideKind ?? DetectedKind`) is `ModelKind.Embedding` or `ModelKind.Reranker`. It also excludes embedding/reranker names as a defensive fallback when no classification row exists. Unknown and Chat models otherwise remain eligible.
- Pick order: the operator's persisted default **iff** it is an installed GGUF chat model, else the most-recently-modified chat model (tie-break by name, case-insensitive).
- Returns `null` when nothing qualifies. The stream service flags `RequiresInstalledChatModel`, and `RunInvocationAsync` throws `NoChatModelInstalledException` *before* any provider call, surfacing a `FailureCategory.ModelNotInstalled` terminal with an actionable "pull a model" message instead of routing a stale id to a dead provider.

Design note (`LocalDefaultChatModelResolver.cs`): this reads **only** `IModelClassificationStore` (a plain DB read), never `IModelClassificationService.ClassifyAsync` — which would miss the cache (`Digest=null`) and re-probe a now-dead Ollama `/api/show` on every local-default send.

### Capability resolution (thinking / tools)

`ResolveModelCapabilitiesAsync` decides `(SupportsThinking, SupportsTools)` once per turn, gating both the `think` field and the tool offer:

- A Codex (cloud) model → declared matrix (`CodexProviderCapabilities.V0`): thinking on, tool-calling per matrix. It is never probed via `/api/show`.
- A non-Ollama local model (a GGUF) → capabilities detected **offline from the chat template** via `IGgufModelCapabilityResolver` (no Ollama probe, no network — critical in desktop mode where there is no Ollama daemon).
- An Ollama-routed model → `IModelClassificationService.ClassifyAsync` (cache-first).
- Any miss → NOT-capable for both: omits `think` (avoids the Ollama HTTP 400 on a non-thinking model) and withholds tools, while still allowing a plain chat.

## Per-message agent attribution

The assistant placeholder is minted **after** `ResolveTurnAsync` so it can be stamped with the resolved agent's snapshot. Effective-agent precedence: `request.AgentDefinitionId ?? conversation.AgentDefinitionId ?? memoized Default Assistant id`. The placeholder carries `AgentDefinitionId`, `AgentName`, and the effort that actually drives the turn (an agent's pinned effort wins over the request selection). A missing seed / deleted definition degrades to the embedded default persona with the full tool offer and version 1 — never failing the turn. Agent resolution internals (prompt composition, tool intersection, skills, orchestration) live in [Agent Mode](04-agent-mode.md).

## Ordered parts: reasoning ↔ tool ↔ answer

`NodeChatPartAccumulator` (`Services/Chat/Implementation/NodeChatPartAccumulator.cs`) is the single place that observes **both** producers of a turn — the reasoning deltas fanned out by the persistence pump and the tool-call lifecycle events — so it can write an ordered `NodeChatMessagePart` list, which is the **render source of truth on reload**.

Ordering model ("Option A"):

- Reasoning deltas extend the trailing reasoning segment.
- A tool event between two reasoning runs **closes** the current reasoning segment, so one turn can render more than one Thoughts block.
- Tool calls collapse `Requested → Completed` by tool-call id (the completed phase fills the result), guarding the duplicate-tool-part bug class.
- Each part is stamped with the **shared monotonic stream sequence** when opened; `Snapshot()` reconciles global order via `OrderBy(Sequence)`, so the guarantee holds even though the two producers run concurrently under one lock.

In the pump (`NodeChatStreamService.cs`) a reasoning delta is fed into the accumulator under the *same* sequence as its `AssistantDelta` SignalR event, keeping reasoning segments correctly ordered against the concurrently-stamped tool parts. On a terminal, an empty interleave (a plain-text answer) is passed as `null` so persisted parts are left untouched rather than overwritten. The accumulated `parts[]` is serialized into the message's `metadata_json` (see persistence below) and re-rendered by the React `MessageParts` / `ThoughtsSection` / `ToolCallCard` components.

## MCP & local tools offered during a chat run

Tools are offered to the turn only when **all three** hold (`NodeChatStreamService.cs`): the client asked (`request.UseLocalTools`), the node has the tool engine enabled (`runtimeSettings.GetEnableToolsAsync`), and the active model advertises the `tools` capability (`resolution.SupportsTools`).

`LocalToolOfferProvider` (`Services/Chat/Implementation/LocalToolOfferProvider.cs`) builds the offer:

- Built-in agent tool descriptors + the read-only coder tools (`list_files` / `read_file` / `search_text`) + **live MCP tools** + enabled, acknowledged **Custom Tools** (read and sorted so the same catalog yields a byte-identical offer → stable config hash). Custom Tools additionally require the node-level `CustomToolsEnabled` switch (default off), an agent allow-list assignment, and unconditional approval wrapping. A `Fixed` tool may reuse an explicit approval for the conversation until its version changes; a `Parameterized` tool re-prompts for every model-selected argument set. See [Agent Mode](04-agent-mode.md#custom-tools-execution-boundary).
- High-risk tools (`run_in_agent_home`, the coder tools, every MCP tool) are **permission-gated**: withheld unless the active model is in the `AgentHome:ToolCapableModels` allow-list (`LocalToolOfferProvider.IsToolCapable`). A null/unknown model id is treated as not-capable.
  - **This is a second, independent gate from `resolution.SupportsTools` above, and the two can disagree.** `SupportsTools` is detected from the model's chat template and is what the installed-models `TOOLS` chip reflects; the allow-list is an operator permission list. A correctly-chipped, genuinely tool-capable model still gets no tools if the operator has not listed it — and the shipped default lists none of the models the recommender offers. See `docs/agent-knowledge.md` §"Tool calling has FIVE independent gates" for the full evaluation order.
  - The allow-list is **read live on every offer**, not seeded at startup, so an edit in Node Settings applies to the next turn without a node restart. It resolves through `CachedNodeSettingsStore` (a memory-cache hit that `SaveAsync` re-primes), which is why a per-offer read is affordable.
- `spawn_subagent` is **profile-opt-in only** — never offered on the default/mode-off chat path; it is held out of the whole offer and added back only by `GetOfferedToolsForProfile`.

A bound agent definition narrows this offer to its allowed set (`resolved?.AllowedTools`); an unbound conversation uses the full capability-gated offer. The chosen offer travels in the runtime package as the tool list; the invocation factory resolves matching executables from the registry by name. MCP registration and the tool registry live in [Agent Mode](04-agent-mode.md).

## Plain-chat knowledge-base grounding

Plain chat exposes an opt-in **Use knowledge base** toggle when the node capability is enabled. It is
disabled while no indexed documents exist and hidden in Agent Mode, where the agent reaches the same
data through the gated `search_knowledge_base` / read tools instead of duplicate inline grounding.
The preference is forwarded on sends and regenerations.

For an opted-in plain-chat turn, `NodeChatStreamService` runs hybrid retrieval using the user message,
then `KnowledgeChatContextComposer` prepends the highest-ranked excerpts within a character budget.
Each excerpt is fenced as untrusted data; lower-ranked excerpts are dropped first when the budget is
exhausted. Retrieval failure or an empty result degrades to an ordinary turn rather than failing it.

Grounding follows the same cloud-egress gate as attachments. If the effective model or any
orchestration participant is cloud-hosted and `KnowledgeBase:AllowCloudModelAccess` is false, retrieval
does not run and the user receives a visible withheld-data notice. When grounding succeeds, only the
excerpts actually placed in context are persisted as metadata-only sources. React renders them in a
collapsed **Sources** strip under the assistant answer; selecting a source opens its knowledge-document
drawer. Legacy, ungrounded, and empty-result turns render no strip.

## File attachments

A conversation can carry uploaded file attachments that the model reads as turn context. The path is fully node-local: upload → pure-.NET text extraction → encrypted-at-rest storage → per-turn injection.

Large pasted text takes a different, deliberately smaller path. `Security:MaxMessageSizeKb` caps one inbound message's UTF-8 content at a configurable **256 KiB default** (valid range 1–1,024 KiB). `LocalChatHub.EnsureMessageWithinSizeCap` enforces it before the user row is persisted; SignalR's 512 KiB receive ceiling stays above it so the application can return a readable `HubException`. The conversation-list response exposes the effective cap, and React's `ComposerSizeLimit` uses `TextEncoder` to mirror the UTF-8 count, shows an indicator near the threshold, and refuses an over-limit send. That precheck is advisory only—the hub remains authoritative. Larger documents belong on the attachment upload path.

**Endpoints** (FastEndpoints, Operator-policy, routes in `Endpoints/Common/LocalApiRoutes.cs`):

- `POST chat/conversations/{conversationId}/uploads` (`UploadConversationFileEndpoint.cs`) — multipart single-file upload. It enforces the `SecurityOptions.MaxUploadFileSizeMb` cap, sanitizes the client filename to a leaf (`UploadFileNameSanitizer.ToSafeLeafFileName`, so no client string forms a path), checks the extractor's extension allowlist, runs extraction, and persists. The storage path is server-generated; the original name is kept only as **encrypted display metadata**.
- `GET chat/conversations/{conversationId}/uploads` (`ListConversationFilesEndpoint.cs`) — metadata only, never the raw bytes or extracted text.
- `DELETE chat/conversations/{conversationId}/uploads/{fileId}` (`DeleteConversationFileEndpoint.cs`) — drops the metadata row plus the on-disk encrypted bytes and cached Markdown; 204 on remove, 404 when no such file.

**Pure-.NET extraction** (`Services/DocumentIngestion/DocumentTextExtractor.cs`): readers cover plaintext family (`.txt/.md/.csv/.tsv/.json/.log` … `PlaintextDocumentReader.SupportedExtensions`), `.pdf` (`PdfDocumentReader`), and `.docx` (`DocxDocumentReader`) — no external service, no network. Extraction yields a `DocumentExtractionResult` (status + Markdown + char count); only `Extracted` files later contribute text.

**Encrypted store** (`ConversationUploadedFileStore.cs`): the raw bytes and the cached extracted Markdown are encrypted on disk by `UploadedFileBlobProtector` (`UploadedFileBlobProtector.cs`) — AES-GCM (`nonce ‖ ciphertext ‖ tag`) keyed off the same `INodeSqliteKeyHolder` material as the DB columns, with per-blob associated data binding `conversationId + fileId + columnName` (distinct `file_bytes` / `file_md` column tags) so a blob can't be relocated. The original filename is encrypted into the DB row via `NodeChatDbContext.EncryptUploadedFileName`. See [Security & privacy](12-security-and-privacy.md).

**Two injection modes** (`NodeChatStreamService.cs`): the synthetic prepended context differs by turn mode.

- **Plain chat** inlines the extracted text directly. `BuildAttachmentContextMessageAsync` loads the `AttachmentFileIds` named on the send, keeps only `Extracted` files, reads each decrypted Markdown, and composes one capped context message via `ConversationAttachmentContextComposer.Compose(parts, MaxInlinedAttachmentChars)` prepended to the conversation history (`BuildConversationContext` adds a `historyOffset`). Returns `null` on the common no-attachment path so the prompt stays byte-identical.
- **Agent mode** never inlines content. When the turn offers tools, the sandbox is re-staged with this conversation's attachments and the model is handed a pointer message naming the staged paths — see the sandbox-staging path below.

**Agent-mode sandbox staging:** `IConversationSandboxStager.PrepareConversationAttachmentsAsync` re-stages the node sandbox so it holds **only** this conversation's extracted attachments under the workspace `attachments/` alias, then `BuildAgentAttachmentHint` emits a pointer message listing those staged paths so a weak model reads the right files with its `read_file` / `list_files` / `search_text` tools. Staging is best-effort (`PrepareConversationAttachmentsSafelyAsync` — a staging failure degrades to an un-staged run, never fails the turn). The stager lives in Agent Mode; see [Agent Mode](04-agent-mode.md).

The same `AttachmentFileIds` are re-sent on **every** turn from the React side (`Chat.tsx`) so the server always inlines/stages the conversation's *current* (non-deleted) set. On the React side `useConversationAttachments` calls `ensureConversationId` so a first upload attaches to the **selected** conversation rather than minting a duplicate thread; `usePaneFileDrop` adds container-level drag-and-drop over the chat pane and `ChatAttachmentChips` renders the staged set.

## Voice / text-to-speech output

Assistant answers can be spoken aloud through the browser/operating-system Web Speech implementation. The repository ships no voice model or model-download path, and generated audio is not posted to the node. Available voices, quality, offline support, and any platform-service network behavior remain outside repository control. The backend supplies only the `VoiceFeatureEnabled` node setting; see [React client](10-react-client.md) and [Security & privacy](12-security-and-privacy.md).

The chat-side tap is `useVoicePlayback` (`features/voice/useVoicePlayback.ts`), intentionally **decoupled** from the stream reducer: `Chat.tsx` feeds it the same `ChatStreamingState` it hands to the renderer, and the hook diffs only the **answer** text (never reasoning/tool parts), buffers whole sentences (`SentenceBuffer`), and fire-and-forget-enqueues each completed sentence to the runtime so synthesis never blocks the hot stream loop.

- **Selected voice wins** (`enqueueSentence`): the engine and its language are driven from the selected platform voice stored in `VoicePreferencesStore`, so auto-play matches the settings audition. `detectAnswerLanguage` is only the fallback when no selected voice resolves.
- **Barge-in** (`onTurnStart`): every new send / regenerate / cancel stops current playback and resets the per-turn sentence buffer, so a new turn's audio never trails the previous one.
- **Web Speech voice ID honored:** `WebSpeechProvider` applies the selected voice ID to the platform utterance.

## Per-send advanced sampling

Developer-mode per-send sampling overrides ride the send request end-to-end. The React shape is `ChatSamplingOptions` (`src/features/chat/models/ChatSamplingOptions.ts`): `temperature`, `topP`, `topK`, `minP`, `maxOutputTokens`, `repeatPenalty`, `repeatLastN`, `presencePenalty`, `frequencyPenalty`, `seed`, `stop[]`, `numCtx`. The `samplingFieldGroups` metadata drives the `ChatSamplingOptionsDialog` inputs (ranges, sliders, decimal scale). `toWireSamplingOptions` normalizes finite numbers; the adapter forwards `samplingOptions` only when present (`NodeChatAdapter.ts`).

On the backend the value flows straight onto the runtime package: `LocalChatRuntimePackageRequest(... SamplingOptions: request.SamplingOptions ...)` (`NodeChatStreamService.cs`). The wire JSON field (`samplingOptions`, camelCase) matches the backend `SamplingOptions` record. These overrides are intentionally **not** part of the config hash (per repo memory) so they don't invalidate caching. Regenerate carries the same block: the client passes it as the trailing `RegenerateMessage` hub argument and `NodeChatRegenerationService` puts it on the package, so a rerun uses the knobs the original send used.

## Reasoning effort + cloud clamp

`ReasoningEffortNormalizer` (`Services/Chat/ReasoningEffortNormalizer.cs`) is the single source of truth for the effort vocabulary: `minimal / low / medium / high / xhigh` (graded), `none` (off), and the binary **`on`** sentinel (reason-by-default for a model lacking the Ollama `thinking` capability — the factory omits `think` so the model's built-in reasoning runs). Blank/unrecognized → `null`. Centralization fixed the "`on` emits no reasoning" bug, where the sentinel was silently dropped to `null` by three independent normalize ladders → `think:false`.

Codex-only levels `minimal` and `xhigh` are offered only for Codex models; they must never reach the Ollama `think` wire as a literal level (Ollama 400s). The agent factory maps both to `think:true` on the Ollama path; the Codex boundary's current `MapEffortLevel` maps the preserved `xhigh` value to `ResponseReasoningEffortLevel.High`.

On the React side `clampReasoningEffort` (`stores/NodeChatPreferencesStore.ts`) maps a carried-over effort onto a different model's available set **without collapsing reasoning intent** (e.g. switching from a Codex model to a binary-only local model): a reasoning-OFF source (`none`) stays `none` when offered; any reasoning-ON source maps to the available reasoning-ON level of nearest intensity rank (`xhigh→high`, `minimal→low` onto a graded set; any graded level→`on` onto a binary set), falling back to the set's first entry only when no comparable level exists. The effort that actually drives the turn is persisted on the message metadata (`ReasoningEffort`, `NodeChatStreamService.cs`) so it survives reload.

## Persistence

`NodeChatPersistenceService` (`Services/Chat/Implementation/NodeChatPersistenceService.cs`) is a facade over focused collaborators (`NodeChatConversationCommands`, `NodeChatReadModel`, `NodeChatMessageCommands`, `NodeChatVariantBranchService`, `NodeChatFeedbackStore`), all composed from one `NodeChatPersistenceWriter` that owns per-conversation/per-message write-key serialization. It uses a **raw-ADO** path (`NodeChatPersistenceSql`) for the hot streaming writes.

Message lifecycle (from `NodeChatMessageCommands.cs`): `PersistUserMessageAsync` inserts a Completed user row; `CreateAssistantPlaceholderAsync` inserts a Pending assistant row carrying the agent attribution + effort; `MarkAssistantQueued/Streaming`, `FlushAssistantPartialAsync` (append or replace content/reasoning deltas), then `TerminalizeAssistantMessageAsync` writes the final status, token counts, ordered `parts`, and `generationDurationMs`.

The `metadata_json` blob (`NodeChatMessageMetadata`, serialized by `NodeChatMetadataSerializer.SerializeMetadata`) carries the fields with no dedicated column: `reasoning`, `model`, token counts (`input/output/total/reasoning`), the ordered `parts[]`, `agentDefinitionId`, `agentName`, `reasoningEffort`, and `generationDurationMs`. The conversation's `selected_path_json` stores the `{variantGroupId → selectedMessageId}` map (`SerializeSelectedPath`) that collapses variant siblings to the chosen branch when building context (`BuildConversationContext`).

### Encryption of titles and content (at-rest)

Conversation titles, message **content**, and the **`metadata_json`** blob are all **AES-encrypted at rest**, not stored in plaintext. `NodeChatDbContext` (`Client.Persistence/NodeChatDbContext.cs`) holds the node key via `INodeSqliteKeyHolder` and exposes `EncryptConversationTitle` / `DecryptConversationTitle`, plus `EncryptMessageContent` / `DecryptMessageContent` and `EncryptMessageMetadata` / `DecryptMessageMetadata`. Encryption goes through `NodePayloadProtector.Encrypt/Decrypt` with **per-record AAD**: for a title, `conversationId` is bound as both conversation and record id with column tag `"title"`; for message content/metadata, `conversationId + messageId + "content"` (or `"metadata_json"`). Both the raw-ADO persistence path and the EF interceptors write and read through the identical AAD scheme.

Message content and metadata are the only encrypted columns with legacy plaintext rows on disk, so they carry a **versioned read-both envelope** (`NodeChatContentProtection`): the ciphertext is prefixed with a two-byte header (`0xFE 0x01`) that can never begin a valid UTF-8 plaintext, so a read tells ciphertext apart from a legacy plaintext blob without guessing. A startup migration (`NodeChatContentEncryptionBackfillService`) upgrades any legacy plaintext rows to the envelope in resumable, idempotent batches; the title backfill decrypts through the same read-both path. The node SQLite key itself stays local and is never returned to the browser — see [Security & privacy](12-security-and-privacy.md). Schema/migration details are in [Data & persistence](08-data-and-persistence.md).

## React chat feature (`src/features/chat`)

Organized by concern:

| Folder | Highlights |
|---|---|
| `api/` | `NodeChatAdapter` (REST via hey-api generated clients + the SignalR streaming bridge), `NodeChatConnection` (the persistent local hub connection), `NodeChatMapper` (DTO → view model), `NodeChatStreamGuard` / `NodeChatStreamState` (stream state machine), `useNodeChatConnectionReadiness` |
| `components/` | `ChatInputArea`, `ChatMessage` / `ChatMessageList`, `MessageParts` + `ThoughtsSection` + `ToolCallCard` (ordered-parts rendering), `ChatSourcesStrip`, `AgentSelectorCard`, `ModelSelectorCard`, `ChatSamplingOptionsDialog`, `StreamingIndicator` / `StreamCaret`, `ContextUsageBadge`, `MessageFeedbackControl`, `LocalToolsOverview` |
| `models/` | `ChatModels`, `ChatSamplingOptions`, `MessageParts`, `MessageRevisionGrouping`, `ChatCapabilityGates`, `ContextUsageDerivation` |
| `pages/` | `Chat.tsx` (top-level orchestration), model-picker filters/options |
| `queries/` | `NodeChatQueryKeys`, `useCodexModelOptions` |
| `stores/` | `NodeChatPreferencesStore` (model/effort/local-tools selection + `clampReasoningEffort`), `ChatSamplingPreferencesStore` |

### The streaming bridge & transparent resume

`nodeChatAdapter.sendMessage` (`NodeChatAdapter.ts`) builds a wire request and opens a SignalR stream (`SendMessage`) through `signalRStream`, an `AsyncIterable` that bridges SignalR pushes to `for await`. Its standout behavior is **transparent resume**: if the connection drops mid-stream, rather than failing the turn it waits for reconnect and re-attaches via the hub's `ResumeMessage` keyed by the invocation/request id. Resumed events stamp the invocation id as the message id and are remapped back to the assistant message id the caller renders. A `ResumeMessage` stream that throws "unknown/terminal invocation" completes cleanly (the response already finished server-side) so the caller refetches the persisted conversation instead of showing a spurious failure. Terminal event types (`assistant-completed/cancelled/failed/interrupted`) end the stream. The same machinery serves `regenerateMessage`, where the server mints the sibling variant and the ids are latched from the first event. All REST calls go through hey-api generated clients (`@/core/api/generated`) with `callWithResponseValidation` — see [React client](10-react-client.md) and [API & hubs](09-api-and-hubs.md).

### Stream watchdog & provider self-heal

`guardNodeChatStream` (`features/chat/api/NodeChatStreamGuard.ts`) wraps the stream with two guarantees: events are re-ordered by ascending `sequence` (out-of-order arrivals buffered until the gap fills), and a watchdog fails a silent stream. The timeouts are deliberately **large** because a 20B+ model can take well over the old 30 s to emit the first token during cold prompt processing and reasoning models pause silently mid-answer: `defaultFirstChunkTimeoutMs = 120_000` (no-first-chunk, raised from 30 s) and `defaultInterChunkTimeoutMs = 180_000` (inter-chunk-stall, raised from 60 s). The categorized `StreamWatchdogError` (`no-first-chunk` / `inter-chunk-stall`) surfaces in the UI failure label.

On the backend, a transient drop is recovered without an app restart: `DeferredLlamaServerChatClient` (`Providers.LlamaServer/Implementation/DeferredLlamaServerChatClient.cs`) binds its cached MEAI adapter to a specific llama-server endpoint. If that process is gone when a request is sent (variant switched, or the server crashed → the socket is refused) and **no output has streamed yet**, it drops the cached adapter, re-asks the supervisor to ensure a running server (which re-spawns it), and retries the request **once** (`GetResponseAsync`/`GetStreamingResponseAsync`). It also holds a per-request inference **lease** so a *graceful* operator eject drains the turn rather than killing it; a *force* eject that kills the process mid-request is distinguished from a transient crash (via the lease's `WasEjected`) and surfaces `LlamaServerModelEjectedException` → classified `FailureCategory.Cancelled` with a truthful "ejected by the operator" message, **not** retried. See [Local runtime & providers](03-local-runtime-and-providers.md).

## Seams & invariants a maintainer must respect

- **`RuntimeChatClient` re-selects per call** — never cache the resolved inner client; never dispose it at the call boundary.
- **Local default never resolves to Ollama** — only installed GGUF chat models; a no-model state must surface `ModelNotInstalled`, not a dead-provider error.
- **Run lifecycle is not cancelled directly by the connection token** — a user Stop is immediate; a disconnect detaches and is cancelled only after the configured grace. Unsubscribe handlers *after* awaiting run + pump.
- **Disconnect is detach-then-grace, not immediate cancellation.** Preserve `TrackAttachment`, live re-reads of `DetachedGraceSeconds`, and resume entry points; user Stop remains immediate while grace expiry is attributed separately.
- **Deltas stay delta-only and queues stay bounded.** Do not put accumulated content on `assistant-delta`, block persistence on a slow consumer, or drop without forcing snapshot reconciliation.
- **The hub owns the inbound UTF-8 size cap.** Keep the configurable 256 KiB default below SignalR's 512 KiB transport ceiling; the React precheck improves UX but is not enforcement.
- **Ordered parts are the reload source of truth** — every part must be stamped with the shared stream sequence; never overwrite persisted parts with an empty snapshot.
- **The tool offer must be byte-identical for the same catalog state** (stable config hash) — preserve sorting and capability gating; keep `spawn_subagent` profile-opt-in only.
- **Plain-chat knowledge grounding is opt-in and source-exact** — apply the cloud-egress gate before retrieval and persist/render only excerpts actually placed in the model context.
- **Effort vocabulary lives in one place** (`ReasoningEffortNormalizer`); Codex-only levels must never reach the Ollama `think` wire as literals.
- **Titles, content, and `metadata_json` are ciphertext at rest** with per-record AAD; content/metadata use the versioned read-both envelope (`NodeChatContentProtection`) so a legacy plaintext row stays readable until the startup migration re-encrypts it. The node key never leaves the node.
- **Attachment bytes and extracted Markdown are encrypted at rest too** (`UploadedFileBlobProtector`, per-blob AAD); plain chat inlines only `Extracted` text capped to `MaxInlinedAttachmentChars`, agent mode never inlines content — it stages files into the sandbox and points at staged paths.
- **Voice synthesis is browser-side and answer-only** — feed `useVoicePlayback` only the answer text, never reasoning/tool parts; barge-in must reset on every send/regenerate/cancel; the selected voice's language always wins over auto-detection.

## Related pages

- [Architecture overview](01-architecture-overview.md)
- [Local runtime & providers](03-local-runtime-and-providers.md)
- [Agent Mode](04-agent-mode.md)
- [Data & persistence](08-data-and-persistence.md)
- [API & hubs](09-api-and-hubs.md)
- [React client](10-react-client.md)
- [Security & privacy](12-security-and-privacy.md)
