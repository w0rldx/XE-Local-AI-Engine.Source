# Chat Subsystem

> Last reviewed: 2026-06-27 · Code-grounded.

The chat subsystem is the node's interactive conversation surface: a React feature (`src/features/chat`) that streams turns over a local SignalR hub into a backend pipeline (`Client.Application/Services/Chat`) which resolves a model + agent per turn, runs the Microsoft Agent Framework loop through a single re-selecting `IChatClient`, and persists every turn to encrypted SQLite. This page traces a turn end-to-end: model resolution (including the "local default → installed GGUF chat model" rule), the streaming/persistence pump, ordered reasoning↔tool↔answer parts, per-send sampling, per-message agent attribution, reasoning-effort clamping for cloud models, file attachments (encrypted upload → pure-.NET extraction → plain-chat inlining or agent-mode sandbox staging), browser-side voice / text-to-speech output, the client stream watchdog + provider self-heal, and the at-rest encryption of titles and content. Provider plumbing lives in [Local runtime & providers](03-local-runtime-and-providers.md); agent resolution in [Agent Mode](04-agent-mode.md); persistence/migrations in [Data & persistence](08-data-and-persistence.md); hubs/endpoints in [API & hubs](09-api-and-hubs.md).

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
| Reasoning-effort vocabulary | `ReasoningEffortNormalizer` (backend) / `clampReasoningEffort` (React) |
| File attachments (store + extraction) | `ConversationUploadedFileStore` + `DocumentTextExtractor` (`Client.Application/Services/DocumentIngestion`) |
| Plain-chat attachment inlining | `ConversationAttachmentContextComposer` (`Services/Chat/Implementation`) |
| Agent-mode attachment staging | `IConversationSandboxStager` (see [Agent Mode](04-agent-mode.md)) |
| Browser voice / TTS tap | `useVoicePlayback` + `VoiceRuntime` (`Client.React`, see [React client](10-react-client.md)) |
| Stream watchdog | `guardNodeChatStream` (`features/chat/api/NodeChatStreamGuard.ts`) |
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
              7. PumpInvocationStatesAsync: deltas ─▶ AssistantDelta, tool events ─▶ ToolCall*
              8. terminal ─▶ AssistantCompleted | Failed | Cancelled | Interrupted
```

`SendMessageAsync` (`NodeChatStreamService.cs:45`) validates non-empty content then delegates to the `IAsyncEnumerable` core. Each step `yield return`s a `ChatStreamEvent` tagged with a monotonic `sequence` (`NodeChatStreamSequence`) so the browser can order concurrently-produced events. The user-visible SSE order is fixed: `UserMessagePersisted → AssistantPending → AssistantQueued → AssistantStreaming → (AssistantDelta | ToolCall*)* → terminal`.

### The collision lease and connection-independent lifecycle

A turn is **Queued** until `RunInvocationAsync` acquires the shared invocation slot via `eventDispatcher.ReportInvocationAssignedAsync` (`NodeChatStreamService.cs:288`); only then does it transition to **Streaming**. This keeps a turn waiting behind another in-flight invocation honestly "queued" rather than prematurely "streaming".

A critical invariant (documented at `NodeChatStreamService.cs:113-126`, `:239-254`): the **client `cancellationToken` is deliberately NOT linked to the run.** When the browser disconnects, only the SSE-forwarding loop stops; the run and the persistence pump keep going on a separate `runCancellation` token so the runner reaches its true terminal (Completed/Failed) and the pump persists it. A genuine user "stop" is the *only* thing that trips `runCancellation` — routed through `INodeChatStreamCancellationRegistry` from the cancel endpoint, which also cancels the runner so the pump records the real `Cancelled` terminal. The handlers must be unsubscribed *after* awaiting both tasks, or the terminal event can arrive after teardown and the message is falsely persisted as interrupted.

## Model resolution (the per-turn `RuntimeChatClient` seam)

`RuntimeChatClient` (`Services/CloudProviders/Implementation/RuntimeChatClient.cs:19`) is the node's single registered `IChatClient`. It is a thin wrapper that **re-selects cloud-vs-local on every call** (`ResolveActiveClient`, `:83`): it asks `IActiveCloudChatClientFactory.TryCreateActiveCloudChatClient` and uses the cloud client when a cloud provider is selected and usable, otherwise the lazily-created, reused local client. Consequences a maintainer must respect:

- Singletons (the agent factories) capture this wrapper once, but signing in/out of a cloud provider (Codex) takes effect on the **next send** without restarting the node.
- The local client is stable → resolved once and reused. The cloud client is owned and lifecycle-managed by the cloud factory (which caches it on a selection fingerprint and never disposes a swapped-out wrapper mid-flight). `RuntimeChatClient.Dispose` disposes **only** the local client (`:73`); disposing the resolved client at the per-call boundary would be a bug for both paths.
- When a cloud provider is selected but unusable (no Codex session), the factory throws a **typed re-auth error** that propagates to the caller as a re-authenticate prompt — it does **not** silently route local.

### Which model name drives the turn

`NodeChatStreamService.ResolveTurnAsync` (`:564`) computes the active model with the same precedence the model list/picker uses:

1. Explicit `request.Model` (any picked model, including an Ollama model) — honored unchanged.
2. Otherwise the operator's persisted node default (`StoredNodeSettings.DefaultModelName`).
3. Otherwise (the **"Local runtime default"** case, `request.Model` null/blank) → resolved through `ILocalDefaultChatModelResolver`.

### Local-default → installed GGUF chat model

`LocalDefaultChatModelResolver` (`Services/Chat/Implementation/LocalDefaultChatModelResolver.cs:19`) resolves the local default from **installed GGUF (llama.cpp) models only — never Ollama**. Rules:

- Enumerates installed models via `IGgufModelStore.ListInstalledModelsAsync`.
- Excludes only entries whose **persisted effective kind** (`OverrideKind ?? DetectedKind`) is `ModelKind.Embedding`. A model with no classification row (Unknown) or kind Chat stays eligible — matching the chat picker's rule.
- Pick order: the operator's persisted default **iff** it is an installed GGUF chat model, else the most-recently-modified chat model (tie-break by name, case-insensitive).
- Returns `null` when nothing qualifies. The stream service flags `RequiresInstalledChatModel`, and `RunInvocationAsync` throws `NoChatModelInstalledException` *before* any provider call (`:298`), surfacing a `FailureCategory.ModelNotInstalled` terminal with an actionable "pull a model" message instead of routing a stale id to a dead provider.

Design note (`LocalDefaultChatModelResolver.cs:12-17`): this reads **only** `IModelClassificationStore` (a plain DB read), never `IModelClassificationService.ClassifyAsync` — which would miss the cache (`Digest=null`) and re-probe a now-dead Ollama `/api/show` on every local-default send.

### Capability resolution (thinking / tools)

`ResolveModelCapabilitiesAsync` (`:635`) decides `(SupportsThinking, SupportsTools)` once per turn, gating both the `think` field and the tool offer:

- A Codex (cloud) model → declared matrix (`CodexProviderCapabilities.V0`): thinking on, tool-calling per matrix. It is never probed via `/api/show`.
- A non-Ollama local model (a GGUF) → capabilities detected **offline from the chat template** via `IGgufModelCapabilityResolver` (no Ollama probe, no network — critical in desktop mode where there is no Ollama daemon).
- An Ollama-routed model → `IModelClassificationService.ClassifyAsync` (cache-first).
- Any miss → NOT-capable for both: omits `think` (avoids the Ollama HTTP 400 on a non-thinking model) and withholds tools, while still allowing a plain chat.

## Per-message agent attribution

The assistant placeholder is minted **after** `ResolveTurnAsync` so it can be stamped with the resolved agent's snapshot. Effective-agent precedence (`:599-604`): `request.AgentDefinitionId ?? conversation.AgentDefinitionId ?? memoized Default Assistant id`. The placeholder carries `AgentDefinitionId`, `AgentName`, and the effort that actually drives the turn (an agent's pinned effort wins over the request selection, `:101-103`). A missing seed / deleted definition degrades to the embedded default persona with the full tool offer and version 1 — never failing the turn. Agent resolution internals (prompt composition, tool intersection, skills, orchestration) live in [Agent Mode](04-agent-mode.md).

## Ordered parts: reasoning ↔ tool ↔ answer

`NodeChatPartAccumulator` (`Services/Chat/Implementation/NodeChatPartAccumulator.cs:15`) is the single place that observes **both** producers of a turn — the reasoning deltas fanned out by the persistence pump and the tool-call lifecycle events — so it can write an ordered `NodeChatMessagePart` list, which is the **render source of truth on reload**.

Ordering model ("Option A"):

- Reasoning deltas extend the trailing reasoning segment.
- A tool event between two reasoning runs **closes** the current reasoning segment, so one turn can render more than one Thoughts block.
- Tool calls collapse `Requested → Completed` by tool-call id (the completed phase fills the result), guarding the duplicate-tool-part bug class.
- Each part is stamped with the **shared monotonic stream sequence** when opened; `Snapshot()` reconciles global order via `OrderBy(Sequence)`, so the guarantee holds even though the two producers run concurrently under one lock.

In the pump (`NodeChatStreamService.cs:358-366`) a reasoning delta is fed into the accumulator under the *same* sequence as its `AssistantDelta` SSE event, keeping reasoning segments correctly ordered against the concurrently-stamped tool parts. On a terminal, an empty interleave (a plain-text answer) is passed as `null` so persisted parts are left untouched rather than overwritten (`:379-382`). The accumulated `parts[]` is serialized into the message's `metadata_json` (see persistence below) and re-rendered by the React `MessageParts` / `ThoughtsSection` / `ToolCallCard` components.

## MCP & local tools offered during a chat run

Tools are offered to the turn only when **all three** hold (`NodeChatStreamService.cs:178-182`): the client asked (`request.UseLocalTools`), the node has the tool engine enabled (`runtimeSettings.GetEnableToolsAsync`), and the active model advertises the `tools` capability (`resolution.SupportsTools`).

`LocalToolOfferProvider` (`Services/Chat/Implementation/LocalToolOfferProvider.cs:28`) builds the offer:

- Built-in agent tool descriptors + the read-only coder tools (`list_files` / `read_file` / `search_text`) + **live MCP tools** (read and sorted so the same catalog yields a byte-identical offer → stable config hash).
- High-risk tools (`run_in_agent_home`, the coder tools, every MCP tool) are **capability-gated**: withheld unless the active model is in the `AgentHome:ToolCapableModels` allow-list. A null/unknown model id is treated as not-capable (`:114-122`).
- `spawn_subagent` is **profile-opt-in only** — never offered on the default/mode-off chat path; it is held out of the whole offer and added back only by `GetOfferedToolsForProfile`.

A bound agent definition narrows this offer to its allowed set (`resolved?.AllowedTools`); an unbound conversation uses the full capability-gated offer. The chosen offer travels in the runtime package as the tool list; the invocation factory resolves matching executables from the registry by name. MCP registration and the tool registry live in [Agent Mode](04-agent-mode.md).

## File attachments

A conversation can carry uploaded file attachments that the model reads as turn context. The path is fully node-local: upload → pure-.NET text extraction → encrypted-at-rest storage → per-turn injection.

**Endpoints** (FastEndpoints, Operator-policy, routes in `Endpoints/Common/LocalApiRoutes.cs:55-56`):

- `POST chat/conversations/{conversationId}/uploads` (`UploadConversationFileEndpoint.cs:17`) — multipart single-file upload. It enforces the `SecurityOptions.MaxUploadFileSizeMb` cap, sanitizes the client filename to a leaf (`UploadFileNameSanitizer.ToSafeLeafFileName`, so no client string forms a path), checks the extractor's extension allowlist, runs extraction, and persists. The storage path is server-generated; the original name is kept only as **encrypted display metadata**.
- `GET chat/conversations/{conversationId}/uploads` (`ListConversationFilesEndpoint.cs:13`) — metadata only, never the raw bytes or extracted text.
- `DELETE chat/conversations/{conversationId}/uploads/{fileId}` (`DeleteConversationFileEndpoint.cs:12`) — drops the metadata row plus the on-disk encrypted bytes and cached Markdown; 204 on remove, 404 when no such file.

**Pure-.NET extraction** (`Services/DocumentIngestion/DocumentTextExtractor.cs:36-42`): readers cover plaintext family (`.txt/.md/.csv/.tsv/.json/.log` … `PlaintextDocumentReader.SupportedExtensions`), `.pdf` (`PdfDocumentReader`), and `.docx` (`DocxDocumentReader`) — no external service, no network. Extraction yields a `DocumentExtractionResult` (status + Markdown + char count); only `Extracted` files later contribute text.

**Encrypted store** (`ConversationUploadedFileStore.cs:18`): the raw bytes and the cached extracted Markdown are encrypted on disk by `UploadedFileBlobProtector` (`UploadedFileBlobProtector.cs:20`) — AES-GCM (`nonce ‖ ciphertext ‖ tag`) keyed off the same `INodeSqliteKeyHolder` material as the DB columns, with per-blob associated data binding `conversationId + fileId + columnName` (distinct `file_bytes` / `file_md` column tags) so a blob can't be relocated. The original filename is encrypted into the DB row via `NodeChatDbContext.EncryptUploadedFileName`. See [Security & privacy](12-security-and-privacy.md).

**Two injection modes** (`NodeChatStreamService.cs:215-220`): the synthetic prepended context differs by turn mode.

- **Plain chat** inlines the extracted text directly. `BuildAttachmentContextMessageAsync` (`:585`) loads the `AttachmentFileIds` named on the send, keeps only `Extracted` files, reads each decrypted Markdown, and composes one capped context message via `ConversationAttachmentContextComposer.Compose(parts, MaxInlinedAttachmentChars)` prepended to the conversation history (`BuildConversationContext` adds a `historyOffset`, `:561`). Returns `null` on the common no-attachment path so the prompt stays byte-identical.
- **Agent mode** never inlines content. When the turn offers tools, the sandbox is re-staged with this conversation's attachments and the model is handed a pointer message naming the staged paths — see the sandbox-staging path below.

**Agent-mode sandbox staging:** `IConversationSandboxStager.PrepareConversationAttachmentsAsync` re-stages the node sandbox so it holds **only** this conversation's extracted attachments under the workspace `attachments/` alias, then `BuildAgentAttachmentHint` (`:658`) emits a pointer message listing those staged paths so a weak model reads the right files with its `read_file` / `list_files` / `search_text` tools. Staging is best-effort (`PrepareConversationAttachmentsSafelyAsync`, `:639` — a staging failure degrades to an un-staged run, never fails the turn). The stager lives in Agent Mode; see [Agent Mode](04-agent-mode.md).

The same `AttachmentFileIds` are re-sent on **every** turn from the React side (`Chat.tsx:656-658`) so the server always inlines/stages the conversation's *current* (non-deleted) set. On the React side `useConversationAttachments` calls `ensureConversationId` so a first upload attaches to the **selected** conversation rather than minting a duplicate thread; `usePaneFileDrop` adds container-level drag-and-drop over the chat pane and `ChatAttachmentChips` renders the staged set.

## Voice / text-to-speech output

Assistant answers can be spoken aloud. The TTS engine runs **entirely in the browser** (WebGPU Kokoro with a Web Speech fallback ladder) — no audio synthesis touches the backend, so voice adds no new egress channel (the backend only serves a config-only voice manifest; see [React client](10-react-client.md) and [Security & privacy](12-security-and-privacy.md)).

The chat-side tap is `useVoicePlayback` (`features/voice/useVoicePlayback.ts:24`), intentionally **decoupled** from the stream reducer: `Chat.tsx` (`:176`) feeds it the same `ChatStreamingState` it hands to the renderer, and the hook diffs only the **answer** text (never reasoning/tool parts), buffers whole sentences (`SentenceBuffer`), and fire-and-forget-enqueues each completed sentence to the runtime so synthesis never blocks the hot stream loop.

- **Selected voice wins** (`enqueueSentence`, `:41-50`): the engine and its language are driven from the *selected* voice's own language (`VoicePreferencesStore.voiceProfile` → `manifest.defaultVoiceId`), so auto-play matches the node-settings audition exactly — an English answer is never re-routed away from a German voice the user picked. `detectAnswerLanguage` is only the fallback when no voice resolves at all.
- **Barge-in** (`onTurnStart`, `:55-61`): every new send / regenerate / cancel stops current playback and resets the per-turn sentence buffer, so a new turn's audio never trails the previous one.
- **Web Speech voiceId** honored: when the runtime ladder falls through to `WebSpeechProvider` (`core/runtime/VoiceRuntime.ts:81-82`), the selected `voiceId` still drives the platform utterance.

## Per-send advanced sampling

Developer-mode per-send sampling overrides ride the send request end-to-end. The React shape is `ChatSamplingOptions` (`src/features/chat/models/ChatSamplingOptions.ts`): `temperature`, `topP`, `topK`, `minP`, `maxOutputTokens`, `repeatPenalty`, `repeatLastN`, `presencePenalty`, `frequencyPenalty`, `seed`, `stop[]`, `numCtx`. The `samplingFieldGroups` metadata drives the `ChatSamplingOptionsDialog` inputs (ranges, sliders, decimal scale). `toWireSamplingOptions` normalizes finite numbers; the adapter forwards `samplingOptions` only when present (`NodeChatAdapter.ts:130-131`).

On the backend the value flows straight onto the runtime package: `LocalChatRuntimePackageRequest(... SamplingOptions: request.SamplingOptions ...)` (`NodeChatStreamService.cs:196`). The wire JSON field (`samplingOptions`, camelCase) matches the backend `SamplingOptions` record. These overrides are intentionally **not** part of the config hash (per repo memory) so they don't invalidate caching.

## Reasoning effort + cloud clamp

`ReasoningEffortNormalizer` (`Services/Chat/ReasoningEffortNormalizer.cs:24`) is the single source of truth for the effort vocabulary: `minimal / low / medium / high / xhigh` (graded), `none` (off), and the binary **`on`** sentinel (reason-by-default for a model lacking the Ollama `thinking` capability — the factory omits `think` so the model's built-in reasoning runs). Blank/unrecognized → `null`. Centralization fixed the "`on` emits no reasoning" bug, where the sentinel was silently dropped to `null` by three independent normalize ladders → `think:false`.

Codex-only levels `minimal` and `xhigh` are offered only for Codex models; they must never reach the Ollama `think` wire as a literal level (Ollama 400s). The agent factory maps both to `think:true` on the Ollama path; the Codex boundary maps them to `ResponseReasoningEffortLevel` (with `xhigh → High` on the pinned OpenAI 2.10.0 SDK, which has no XHigh member).

On the React side `clampReasoningEffort` (`stores/NodeChatPreferencesStore.ts:61`) maps a carried-over effort onto a different model's available set **without collapsing reasoning intent** (e.g. switching from a Codex model to a binary-only local model): a reasoning-OFF source (`none`) stays `none` when offered; any reasoning-ON source maps to the available reasoning-ON level of nearest intensity rank (`xhigh→high`, `minimal→low` onto a graded set; any graded level→`on` onto a binary set), falling back to the set's first entry only when no comparable level exists. The effort that actually drives the turn is persisted on the message metadata (`ReasoningEffort`, `NodeChatStreamService.cs:101-103`) so it survives reload.

## Persistence

`NodeChatPersistenceService` (`Services/Chat/Implementation/NodeChatPersistenceService.cs:9`) is a facade over focused collaborators (`NodeChatConversationCommands`, `NodeChatReadModel`, `NodeChatMessageCommands`, `NodeChatVariantBranchService`, `NodeChatFeedbackStore`), all composed from one `NodeChatPersistenceWriter` that owns per-conversation/per-message write-key serialization. It uses a **raw-ADO** path (`NodeChatPersistenceSql`) for the hot streaming writes.

Message lifecycle (from `NodeChatMessageCommands.cs`): `PersistUserMessageAsync` inserts a Completed user row; `CreateAssistantPlaceholderAsync` inserts a Pending assistant row carrying the agent attribution + effort; `MarkAssistantQueued/Streaming`, `FlushAssistantPartialAsync` (append or replace content/reasoning deltas), then `TerminalizeAssistantMessageAsync` writes the final status, token counts, ordered `parts`, and `generationDurationMs`.

The `metadata_json` blob (`NodeChatMessageMetadata`, serialized by `NodeChatMetadataSerializer.SerializeMetadata`, `:47`) carries the fields with no dedicated column: `reasoning`, `model`, token counts (`input/output/total/reasoning`), the ordered `parts[]`, `agentDefinitionId`, `agentName`, `reasoningEffort`, and `generationDurationMs`. The conversation's `selected_path_json` stores the `{variantGroupId → selectedMessageId}` map (`SerializeSelectedPath`) that collapses variant siblings to the chosen branch when building context (`BuildConversationContext`, `:509`).

### Encryption of titles and content (at-rest)

Conversation titles, message **content**, and the **`metadata_json`** blob are all **AES-encrypted at rest**, not stored in plaintext. `NodeChatDbContext` (`Client.Persistence/NodeChatDbContext.cs`) holds the node key via `INodeSqliteKeyHolder` and exposes `EncryptConversationTitle` / `DecryptConversationTitle`, plus `EncryptMessageContent` / `DecryptMessageContent` and `EncryptMessageMetadata` / `DecryptMessageMetadata`. Encryption goes through `NodePayloadProtector.Encrypt/Decrypt` with **per-record AAD**: for a title, `conversationId` is bound as both conversation and record id with column tag `"title"`; for message content/metadata, `conversationId + messageId + "content"` (or `"metadata_json"`). Both the raw-ADO persistence path and the EF interceptors write and read through the identical AAD scheme.

Message content and metadata are the only encrypted columns with legacy plaintext rows on disk, so they carry a **versioned read-both envelope** (`NodeChatContentProtection`): the ciphertext is prefixed with a two-byte header (`0xFE 0x01`) that can never begin a valid UTF-8 plaintext, so a read tells ciphertext apart from a legacy plaintext blob without guessing. A startup migration (`NodeChatContentEncryptionBackfillService`) upgrades any legacy plaintext rows to the envelope in resumable, idempotent batches; the title backfill decrypts through the same read-both path. The node SQLite key itself stays local and is never returned to the browser — see [Security & privacy](12-security-and-privacy.md). Schema/migration details are in [Data & persistence](08-data-and-persistence.md).

## React chat feature (`src/features/chat`)

Organized by concern:

| Folder | Highlights |
|---|---|
| `api/` | `NodeChatAdapter` (REST via hey-api generated clients + the SignalR streaming bridge), `NodeChatConnection` (the persistent local hub connection), `NodeChatMapper` (DTO → view model), `NodeChatStreamGuard` / `NodeChatStreamState` (stream state machine), `useNodeChatConnectionReadiness` |
| `components/` | `ChatInputArea`, `ChatMessage` / `ChatMessageList`, `MessageParts` + `ThoughtsSection` + `ToolCallCard` (ordered-parts rendering), `AgentSelectorCard`, `ModelSelectorCard`, `ChatSamplingOptionsDialog`, `StreamingIndicator` / `StreamCaret`, `ContextUsageBadge`, `MessageFeedbackControl`, `LocalToolsOverview` |
| `models/` | `ChatModels`, `ChatSamplingOptions`, `MessageParts`, `MessageRevisionGrouping`, `ChatCapabilityGates`, `ContextUsageDerivation` |
| `pages/` | `Chat.tsx` (top-level orchestration), model-picker filters/options |
| `queries/` | `NodeChatQueryKeys`, `useCodexModelOptions` |
| `stores/` | `NodeChatPreferencesStore` (model/effort/local-tools selection + `clampReasoningEffort`), `ChatSamplingPreferencesStore` |

### The streaming bridge & transparent resume

`nodeChatAdapter.sendMessage` (`NodeChatAdapter.ts:420`) builds a wire request and opens a SignalR stream (`SendMessage`) through `signalRStream` (`:171`), an `AsyncIterable` that bridges SignalR pushes to `for await`. Its standout behavior is **transparent resume**: if the connection drops mid-stream, rather than failing the turn it waits for reconnect and re-attaches via the hub's `ResumeMessage` keyed by the invocation/request id. Resumed events stamp the invocation id as the message id and are remapped back to the assistant message id the caller renders (`:213-215`). A `ResumeMessage` stream that throws "unknown/terminal invocation" completes cleanly (the response already finished server-side) so the caller refetches the persisted conversation instead of showing a spurious failure. Terminal event types (`assistant-completed/cancelled/failed/interrupted`) end the stream. The same machinery serves `regenerateMessage` (`:434`), where the server mints the sibling variant and the ids are latched from the first event. All REST calls go through hey-api generated clients (`@/core/api/generated`) with `callWithResponseValidation` — see [React client](10-react-client.md) and [API & hubs](09-api-and-hubs.md).

### Stream watchdog & provider self-heal

`guardNodeChatStream` (`features/chat/api/NodeChatStreamGuard.ts:63`) wraps the stream with two guarantees: events are re-ordered by ascending `sequence` (out-of-order arrivals buffered until the gap fills), and a watchdog fails a silent stream. The timeouts are deliberately **large** because a 20B+ model can take well over the old 30 s to emit the first token during cold prompt processing and reasoning models pause silently mid-answer: `defaultFirstChunkTimeoutMs = 120_000` (no-first-chunk, raised from 30 s) and `defaultInterChunkTimeoutMs = 180_000` (inter-chunk-stall, raised from 60 s) (`:32-33`). The categorized `StreamWatchdogError` (`no-first-chunk` / `inter-chunk-stall`, `:3,11`) surfaces in the UI failure label.

On the backend, a transient drop is recovered without an app restart: `DeferredLlamaServerChatClient` (`Providers.LlamaServer/Implementation/DeferredLlamaServerChatClient.cs`) binds its cached MEAI adapter to a specific llama-server endpoint. If that process is gone when a request is sent (variant switched, or the server crashed → the socket is refused) and **no output has streamed yet**, it drops the cached adapter, re-asks the supervisor to ensure a running server (which re-spawns it), and retries the request **once** (`GetResponseAsync`/`GetStreamingResponseAsync`). It also holds a per-request inference **lease** so a *graceful* operator eject drains the turn rather than killing it; a *force* eject that kills the process mid-request is distinguished from a transient crash (via the lease's `WasEjected`) and surfaces `LlamaServerModelEjectedException` → classified `FailureCategory.Cancelled` with a truthful "ejected by the operator" message, **not** retried. See [Local runtime & providers](03-local-runtime-and-providers.md).

## Seams & invariants a maintainer must respect

- **`RuntimeChatClient` re-selects per call** — never cache the resolved inner client; never dispose it at the call boundary.
- **Local default never resolves to Ollama** — only installed GGUF chat models; a no-model state must surface `ModelNotInstalled`, not a dead-provider error.
- **Run lifecycle is independent of the client connection** — only a real user cancel trips `runCancellation`; unsubscribe handlers *after* awaiting run + pump.
- **Ordered parts are the reload source of truth** — every part must be stamped with the shared stream sequence; never overwrite persisted parts with an empty snapshot.
- **The tool offer must be byte-identical for the same catalog state** (stable config hash) — preserve sorting and capability gating; keep `spawn_subagent` profile-opt-in only.
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
