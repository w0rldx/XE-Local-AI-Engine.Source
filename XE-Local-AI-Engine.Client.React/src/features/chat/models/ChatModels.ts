import type { ReactNode } from "react";
import type { ChatCommandOption } from "@/features/chat/models/SlashCommandModels";

// Re-exported so chat's own call sites keep importing from here; the type itself lives in core because `agents` also
// depends on it (see ReasoningEffort.ts for the effort-level doc comment).
import type { ReasoningEffort } from "@/core/models/ReasoningEffort";
export type { ReasoningEffort };
// Type-only import (erased at runtime, so no models→api cycle): the `ask_user` wire shape is owned by the wire
// module so a backend field rename stays a one-line fix.
import type { PendingUserQuestion } from "@/features/chat/api/AskUserQuestionWire";
import type { ChatAttachment, PendingAttachmentUpload } from "@/features/chat/models/ChatAttachmentModels";

export type ChatRole = "user" | "assistant" | "system" | "tool";

export type MessageStatus = "pending" | "queued" | "streaming" | "completed" | "cancelled" | "failed" | "interrupted";

export type ChatOrigin = "local" | "remote";

export type ToolCallState = "requesting" | "waiting" | "received" | "failed";

export type ChatMessagePartKind = "reasoning" | "tool" | "text" | "notice";

/** A folded "Thoughts" run. Each reasoning segment renders as its own block (Option A interleave). */
export interface ChatReasoningPart {
	kind: "reasoning";
	// Stable per segment (`${messageId}:${sequence}` live, `${messageId}:${sequence}` on reload).
	id: string;
	// Wire ordering key (the monotonic stream `sequence` at which this segment opened).
	sequence: number;
	text: string;
}

/** A single tool call, collapsed requested→completed by tool-call id, rendered as one state-driven card. */
export interface ChatToolPart {
	kind: "tool";
	// Tool-call id (stable across requested/completed so the card never duplicates).
	id: string;
	sequence: number;
	name: string;
	state: ToolCallState;
	args?: string;
	result?: string;
	requiresApproval?: boolean;
	// When set, the tool is paused awaiting the operator's decision; this is the approval request id the
	// Approve/Deny controls post to the loopback resolve endpoint. Transient, live-only: cleared once the tool
	// completes (approved) or is rejected, and never present on a reloaded/persisted turn.
	pendingApprovalRequestId?: string;
	// The backend's per-request answer to whether an "approve for this session" decision can be remembered for this
	// exact call. Preferred over the tool catalog's tool-identity flag; undefined when the backend did not resolve it
	// (a reconnect replay), in which case the card falls back to the catalog.
	pendingApprovalSessionScopeEligible?: boolean;
	// When set, this is an `ask_user` call parked on the operator's answer: the question payload the inline answer
	// card renders. Transient and live-only on exactly the same terms as `pendingApprovalRequestId` — cleared when
	// the tool call completes/fails or the turn terminalizes, and never present on a reloaded/persisted turn.
	pendingQuestion?: PendingUserQuestion;
}

/**
 * Forward-compat: an interleaved mid-turn answer/narration segment. The primary local-model case is
 * reasoning↔tool interleave plus a single trailing answer (rendered from `message.content`), so `text`
 * parts are rare; they exist so multi-step narration round-trips cleanly when the backend emits it.
 */
export interface ChatTextPart {
	kind: "text";
	id: string;
	sequence: number;
	text: string;
}

/**
 * A non-fatal "turn notice" (model substitution, tool disabled, history truncated) surfaced by the backend as its
 * own stream event / persisted part kind. Rendered as a small muted system-style row — distinct from an error and
 * from the plain answer text — never a fatal state for the turn.
 */
export interface ChatNoticePart {
	kind: "notice";
	id: string;
	sequence: number;
	// One of "ModelSubstituted" | "ToolDisabled" | "HistoryTruncated" (server enum name, used to pick an icon).
	noticeKind: string;
	// Sanitized, user-facing sentence from the backend — displayed verbatim, never re-translated.
	text: string;
	// Optional structured detail beside the sentence: a stable machine code or short identifier naming why the notice
	// fired (e.g. the kebab-case adaptive-effort dispatch reason). Displayed verbatim, never re-translated; undefined
	// on notices that carry none.
	detail?: string;
}

export type ChatMessagePart = ChatReasoningPart | ChatToolPart | ChatTextPart | ChatNoticePart;

/**
 * One knowledge-base excerpt that grounded a plain-chat assistant turn. Rendered in the collapsible
 * "Sources" strip under the answer. Carries only non-sensitive provenance (ids, derived title/section, fused score);
 * no chunk body text rides here. Maps from the persisted metadata blob via NodeChatMapper.
 */
export interface ChatMessageSource {
	documentId: string;
	chunkId: string;
	title: string;
	section?: string;
	score: number;
}

export type TimelineEventType =
	| "ToolCall"
	| "ToolResult"
	| "ContextRetrieval"
	| "WorkflowStepStarted"
	| "WorkflowStepCompleted"
	| "WorkflowStepFailed"
	| "ContentGeneration"
	| "Error";

export interface ChatMessageModel {
	id: string;
	conversationId: string;
	// The run's invocation/request id when this is a (re)generated assistant turn. Used to re-attach to a
	// server-driven run via the resume registry (e.g. streaming a regenerate's variant).
	requestId?: string;
	role: ChatRole;
	content: string;
	reasoning?: string;
	status: MessageStatus;
	createdAt: string;
	updatedAt?: string;
	sortOrder: number;
	model?: string;
	error?: string;
	origin?: ChatOrigin;
	inputTokens?: number;
	outputTokens?: number;
	totalTokens?: number;
	reasoningTokens?: number;
	parentMessageId?: string;
	variantGroupId?: string;
	// Ordered interleave (reasoning → tool → reasoning → …) for an assistant turn. Drives the in-order render of
	// the reasoning/tool region; the trailing answer still renders from `content`. Absent for user/legacy turns.
	parts?: ChatMessagePart[];
	// Node-local feedback on this assistant turn, carried on the message read DTO (feedback flow). Undefined when
	// no feedback has been recorded; presence drives the feedback control's active state.
	feedbackRating?: ChatFeedbackRating;
	feedbackComment?: string;
	// Agent attribution fields stamped at send time (ride metadata_json blob, no migration).
	// Absent for legacy turns and user messages. ChatMessage shows agentName ?? t("defaultAgentName") for
	// assistant turns so every response carries a visible attribution even without a persisted name.
	agentName?: string;
	agentDefinitionId?: string;
	// The reasoning effort that was ACTUALLY used for this turn (persisted in metadata_json, ground truth).
	// Distinct from the live composer `reasoningEffort` prop (that's the current picker selection).
	// null/absent = legacy turn or user message. "none" = reasoning explicitly disabled.
	reasoningEffort?: ReasoningEffort;
	// Backend-exact wall-clock generation duration in milliseconds (persisted in metadata_json, same blob as
	// agentName/reasoningEffort — no migration). Drives the optional tokens/sec attribution. Absent for legacy
	// turns and user messages → no tps shown.
	generationDurationMs?: number;
	// Knowledge-base excerpts that grounded this plain-chat turn (persisted in metadata_json, same blob as the
	// attribution fields — no migration). Drives the collapsible "Sources" strip. Absent/empty for legacy turns,
	// non-knowledge turns, and user messages.
	sources?: ChatMessageSource[];
}

export type ChatFeedbackRating = "up" | "down";

export interface ChatMessageFeedback {
	messageId: string;
	conversationId: string;
	rating: ChatFeedbackRating;
	comment?: string;
	createdAt: string;
	updatedAt: string;
}

export interface ChatMessageRevisions {
	messageId: string;
	variantGroupId?: string;
	variants: ChatMessageModel[];
}

export interface ChatConversationModel {
	id: string;
	title: string;
	createdAt: string;
	updatedAt: string;
	lastActivity?: string;
	lastMessagePreview?: string;
	isPinned?: boolean;
	isArchived?: boolean;
	// When true this conversation is "temporary" — it won't teach the bound agent new adaptive memory (existing
	// memory is still used). Toggled per-conversation in the chat header; defaults from the agent's defaultTemporaryChat.
	memoryExcluded?: boolean;
	origin?: ChatOrigin;
	branchOfConversationId?: string;
	// Persisted selected-path map {variantGroupId -> selectedMessageId} for the conversation tree. Seeds the
	// operator's active-revision selection on load so navigating < N/N > variants survives a reload.
	selectedPath?: Record<string, string>;
	messages: ChatMessageModel[];
}

export interface ChatStreamingState {
	conversationId: string;
	messageId: string;
	content: string;
	reasoning?: string;
	reasoningOverflowBytes?: number;
	// Ordered interleave parts for the in-flight turn (reasoning segments + tool cards, ordered by `sequence`).
	// Built once per event by the reducer so the live render and the post-reload render are byte-identical.
	parts?: ChatMessagePart[];
	// The assistant turn's own start timestamp (server-stamped when available). Used to label the
	// transient streaming placeholder so it does not borrow the conversation's last-updated time.
	startedAt?: string;
	isActive: boolean;
	isDelayed?: boolean;
	// True while the assistant turn is queued behind another active invocation (before it starts streaming).
	isQueued?: boolean;
	// Pre-first-token runtime phase ("preparing_runtime" | "loading_model" | "generating"), set by an assistant-phase
	// stream event. Drives the "Loading model…" indicator during a local cold load; clears once content lands.
	runtimePhase?: string;
	error?: string;
	failureCategory?: string;
	inputTokens?: number;
	outputTokens?: number;
	totalTokens?: number;
	reasoningTokens?: number;
}

export interface ChatTimelineEntry {
	id: string;
	messageId?: string;
	invocationId?: string;
	type: TimelineEventType;
	title?: string;
	content?: string;
	toolName?: string;
	toolArgs?: string;
	toolResult?: string;
	state?: ToolCallState;
	// Carried from the tool-call event so the rendered tool call can surface the approval requirement and state.
	requiresApproval?: boolean;
	createdAt: string;
}

export interface ChatToolCall {
	id: string;
	name: string;
	state: ToolCallState;
	args?: string;
	result?: string;
	duration?: string;
	// Tool-level metadata indicating whether execution requires explicit approval. Carried through the stream reducer
	// so the tool card and tools overview can surface the requirement while the approval UI handles pending decisions.
	requiresApproval?: boolean;
}

export interface ModelOption {
	value: string;
	label: string;
	displayName?: string;
	// GRADED reasoning: the model advertises the Ollama `thinking` capability, i.e. a switchable think:<level>
	// control. Drives the graded reasoning-effort menu (none/low/medium/high).
	isReasoningModel?: boolean;
	// NATIVE reasoning: the model reasons on a channel baked into its chat template with no graded switch (OpenAI
	// harmony / gpt-oss). Mutually exclusive with `isReasoningModel`. Drives its OWN picker badge, but deliberately
	// NOT the effort vocabulary — a native model keeps the binary On/Off set, where "on" means "omit the think field
	// and let the template's built-in reasoning run".
	isNativeReasoningModel?: boolean;
	// Whether a model that reasons also honours a GRADED effort level. Splits the two shapes `isReasoningModel` alone
	// conflates for an externally served model: an endpoint that reads `reasoning_effort` gets the graded menu, one
	// that reasons on its own terms gets the binary On/Off set, because offering it levels it ignores is a menu whose
	// entries do nothing. Undefined means "not declared" — every non-external provider, whose effort vocabulary is
	// decided by `isCloud` and `isReasoningModel` exactly as before.
	isReasoningEffortCapable?: boolean;
	// Whether the model advertises the Ollama `tools` capability. Drives whether the composer offers the
	// local-tool controls (gated together with the node-wide capability). Undefined on the local-default
	// option (the runtime picks a concrete model later), so callers treat undefined as "not tool-capable".
	isToolCapable?: boolean;
	// Whether the model has a local mmproj vision projector (backend `isMultimodalCapable`). Drives whether the
	// composer offers image attachments for this model (gated together with the node-wide capability). Undefined on
	// the local-default option (the runtime picks a concrete model later), so callers treat undefined as "not
	// multimodal".
	isMultimodal?: boolean;
	isAvailable: boolean;
	statusLabel?: string;
	// True for cloud-provider models (e.g. Codex/OpenAI). Drives the "Cloud (Codex)" section in the picker
	// and the egress cue badge. Local models never set this; absence is equivalent to false.
	isCloud?: boolean;
	// The runtime that serves this local model ("Ollama" / "llamacpp"), straight from the list entry's Provider.
	// Used to gate which selections poll the model-details endpoint. Undefined on the local-default sentinel option
	// (the runtime resolves a concrete model later) and on cloud options (gated by isCloud instead).
	provider?: string;
	// External-provider connection this model belongs to (`provider === "external"` only). The picker groups external
	// models one section per connection, which `provider` alone cannot express — every connection shares that one tag.
	externalConnectionId?: string;
	externalConnectionName?: string;
	// The connection's OPERATOR-DECLARED trust ("local" | "cloud"), straight from the list entry. Deliberately distinct
	// from `isCloud`, which selects the Codex reasoning-effort vocabulary (minimal/xhigh) that external models must
	// never be offered: this only decides which section an external model lands in and which egress cue it carries.
	declaredLocality?: string;
}

export interface ContextUsageModel {
	usedTokens?: number;
	maxTokens?: number;
	isAuthoritative: boolean;
	modelLabel: string;
	nodeLabel: string;
}

export interface ChatInputStatus {
	isSending: boolean;
	chatInputDisabled?: boolean;
	modelSelectorDisabled?: boolean;
	sendDisabled?: boolean;
	/** Renders the agent picker read-only. Set by an owner that pins the agent (see {@link ChatScope}). */
	agentSelectorDisabled?: boolean;
}

/**
 * Embeds the chat page inside a feature that OWNS a conversation (today: a work session). Everything the chat page
 * does — the readiness gate, the streaming fold, the tool timeline, the cold-load re-attach — stays in `Chat`; this
 * prop only pins the view and redirects the composer. `/chat` passes nothing and behaves exactly as before.
 */
export interface ChatScope {
	/** Pin the view to exactly this conversation: no sidebar, no selection writes to the global preference store. */
	readonly conversationId: string;
	/** Owner-pinned agent. Forces agent mode on and renders the agent + model selectors read-only. */
	readonly pinnedAgentId?: string;
	/** Bumped by the owner when a NEW server-side turn starts on the same conversation; re-arms the re-attach. */
	readonly resumeNonce?: number;
	/**
	 * Composer target. When set, the composer posts here instead of starting a chat invocation — the owner's
	 * supervisor stays the single writer of invocations on this conversation. A REJECTED promise keeps the draft.
	 */
	readonly onSendOverride?: (content: string) => Promise<void>;
	/** Stop-button target (a work session maps it to pause). */
	readonly onStopOverride?: () => void;
	/** Disables the composer (a terminal session takes no further input). */
	readonly composerDisabled?: boolean;
	/** The parent owns the full-height frame, so `Chat` renders bare. */
	readonly embedded?: boolean;
}

// The conversation-list fetch, envelope and all: the list itself plus the node-level chat facts the endpoint reports
// beside it. It is the chat page's unconditional first GET, which is why the composer's message-size limit rides it
// (see ComposerSizeLimit). `maxMessageSizeKb` is absent when the node reports none; the composer then runs no
// pre-check and an oversized send is rejected by the hub exactly as before.
export interface ChatConversationListModel {
	conversations: ChatConversationModel[];
	maxMessageSizeKb?: number;
}

// Result of a manual, non-destructive compaction. `outcome` mirrors the backend ConversationCompactionOutcome names
// ("Compacted", "NothingToCompact", "NoLocalModel", "SummarizerReturnedNothing", "ConversationNotFound"); the remaining
// fields are populated only when a synopsis was produced.
export interface ChatCompactionResult {
	outcome: string;
	summary?: string;
	coversToSequence?: number;
	messagesFolded: number;
	updatedAtUtc?: number;
	// The local model that produced the synopsis, and whether it differs from the user's selection (true only when a
	// cloud/unknown selection was downgraded to a node-local model). Drives the "summarized on-device" notice.
	modelUsed?: string;
	usedFallbackModel: boolean;
}

// Shared agent option type used by Chat.tsx (derivation), ChatDisplayShellProps, ChatInputArea, and AgentSelectorCard.
// The single derivation site is Chat.tsx; all downstream components receive it as a prop.
export interface AgentOption {
	readonly id: string;
	readonly name: string;
	readonly description: string;
	readonly kind: "Single" | "Orchestrator";
	readonly modelProfile: string | null;
	// Whether this agent has adaptive memory enabled. Gates the temporary-chat toggle in the chat header — the toggle
	// only renders when the bound agent learns memory at all.
	readonly playbookEnabled: boolean;
}

// Matches the backend AgentDefaults.DefaultAgentName seeded slug. Used to exclude the Default Assistant from
// the agent picker so the user never selects it explicitly (mode-off reproduces it transparently).
// The name comparison below is the single exclusion site.
export const DEFAULT_ASSISTANT_NAME = "Default Assistant";

export interface ChatUiCapabilities {
	readonly showLocalToolControls: boolean;
	readonly showToolApprovalControls: boolean;
	readonly showConversationFeedbackControls: boolean;
	readonly showEncryptedConversationControls: boolean;
	readonly showClientNodeRoutingControls: boolean;
	readonly showFileAttachmentControls: boolean;
	readonly showImageAttachmentControls: boolean;
	// When true the chat composer renders the agent-mode toggle + agent picker. Derived from the node's
	// agentManagement surface capability (see ChatCapabilityGates.buildChatUiCapabilities).
	readonly showAgentControls: boolean;
	// When true the chat composer renders the voice controls (toggle, profile, rate) + per-message Play. Derived
	// from the node `voice` surface flag AND the operator-owned manifest.Enabled (see buildChatUiCapabilities).
	// Voice UI is additionally dev-gated at the render site.
	readonly showVoiceControls: boolean;
	// When true the chat composer renders the "Use Knowledge Base" toggle (opt-in plain-chat grounding). Derived
	// from the node's knowledgeBase surface capability (see ChatCapabilityGates.buildChatUiCapabilities).
	readonly showKnowledgeBaseControls: boolean;
}

export interface ChatDisplayShellProps {
	conversations: ChatConversationModel[];
	selectedConversationId: string;
	modelOptions: ModelOption[];
	// Cloud (Codex) model options forwarded from Chat.tsx → ChatDisplayShell → ChatInputArea → ModelSelectorCard.
	// Optional; absent or empty hides the cloud section in the picker.
	cloudModelOptions?: ModelOption[];
	selectedModel: string;
	reasoningEffort: ReasoningEffort;
	availableReasoningEfforts: ReasoningEffort[];
	// Whether the active model advertises the Ollama `tools` capability. Gated together with the node-wide
	// capability to decide whether the composer offers the local-tool controls. Defaults to false (safe).
	activeModelToolCapable?: boolean;
	toolsEnabled?: boolean;
	// Whether the active model has a local mmproj vision projector. Gated together with the node-wide capability
	// to decide whether the composer offers image attachments. Defaults to false (safe).
	activeModelMultimodal?: boolean;
	// Opt-in knowledge-base grounding for plain chat; forwarded to the composer's "Use Knowledge Base" toggle.
	knowledgeBaseEnabled?: boolean;
	// Whether the node has at least one indexed knowledge document; gates whether the composer's KB toggle is enabled
	// (an empty corpus disables it with a "no documents" tooltip). Defaults to true when absent.
	knowledgeBaseHasDocuments?: boolean;
	contextUsage?: ContextUsageModel;
	// The node's message-size cap in KB, forwarded to the composer's size pre-check. Absent → no pre-check.
	maxMessageSizeKb?: number;
	streamingMessage?: ChatStreamingState;
	timelineEntries?: ChatTimelineEntry[];
	capabilities: ChatUiCapabilities;
	inputStatus: ChatInputStatus;
	conversationSearchQuery?: string;
	showArchivedConversations?: boolean;
	mutatingConversationId?: string;
	onSelectConversation: (conversationId: string) => void;
	onCreateConversation: () => void;
	onToggleConversationList: () => void;
	onModelChange: (model: string) => void;
	onReasoningEffortChange: (effort: ReasoningEffort) => void;
	onToggleTools?: () => void;
	onToggleKnowledgeBase?: () => void;
	agentControlsAvailable?: boolean;
	agentModeEnabled?: boolean;
	selectedAgentId?: string;
	agentOptions?: readonly AgentOption[];
	commandOptions?: readonly ChatCommandOption[];
	// Single merged agent control: "" => Default Assistant (agent mode off); any other id => enable mode + stamp it.
	onSelectAgent?: (agentId: string) => void;
	// Conversation file attachments (chip row + upload picker in the composer). Wired from Chat.tsx via
	// useConversationAttachments; only rendered behind the showFileAttachmentControls capability gate.
	attachments?: readonly ChatAttachment[];
	pendingUploads?: readonly PendingAttachmentUpload[];
	onUploadFiles?: (files: File[]) => void;
	onRemoveAttachment?: (fileId: string) => void;
	// Returning a promise defers the draft clear until it RESOLVES (a rejection keeps the draft) — the scoped
	// composer posts over REST, where a rejected follow-up must not vanish. A void return clears immediately.
	onSend: (content: string, effort: ReasoningEffort, model: string) => void | Promise<void>;
	onCancel: () => void;
	onRegenerate?: (messageId: string) => void;
	onConversationSearchChange?: (query: string) => void;
	onToggleShowArchivedConversations?: (showArchived: boolean) => void;
	onRenameConversation?: (conversationId: string, title: string) => void;
	onToggleConversationPinned?: (conversationId: string, isPinned: boolean) => void;
	onToggleConversationArchived?: (conversationId: string, archived: boolean) => void;
	// Whether the bound agent has adaptive memory enabled. When true (and the handler is present) the header renders
	// the per-conversation temporary-chat toggle; otherwise the toggle is hidden (nothing to suppress).
	boundAgentMemoryEnabled?: boolean;
	// Toggle the selected conversation "temporary" (memory-excluded). Only wired/rendered when the bound agent has
	// adaptive memory enabled.
	onToggleConversationMemoryExcluded?: (conversationId: string, memoryExcluded: boolean) => void;
	onDeleteConversation?: (conversationId: string, skipConfirm: boolean) => void;
	onBranchFromMessage?: (messageId: string) => void;
	activeRevisionByGroup?: Readonly<Record<string, string>>;
	onSelectRevision?: (variantGroupId: string, messageId: string) => void;
	feedbackByMessageId?: Readonly<Record<string, ChatMessageFeedback>>;
	pendingFeedbackMessageId?: string;
	onSubmitFeedback?: (messageId: string, rating: ChatFeedbackRating, comment: string | undefined) => void;
	conversationListCollapsed?: boolean;
	// Drops the conversation column (and its header toggle) entirely. `conversationListCollapsed` only shrinks the
	// sidebar to an icon rail, which is wrong for an owner-pinned conversation that has no list to pick from.
	hideConversationList?: boolean;
	// The conversation belongs to a work session (a {@link ChatScope} owner). Forwarded to the message list so a
	// step that ended on its own provider-call cap renders as a neutral notice instead of a red failure.
	isWorkSessionConversation?: boolean;
	disabledNotice?: ReactNode;
	// True while the selected conversation's full payload (with messages) is loading. Forwarded to the
	// message list so the empty-state never flashes over a populated thread during the refetch.
	isLoadingMessages?: boolean;
	// The selected conversation's full payload failed to load (a non-transient getConversation error). Drives the
	// inline error+retry state in the message list, replacing the otherwise-infinite loading spinner.
	messagesLoadFailed?: boolean;
	// Resolved error reason for messagesLoadFailed, shown beneath the generic failure copy for context.
	messagesLoadErrorText?: string;
	// Re-runs the selected-conversation query (refetch). Wired to the Retry action in the message list error state.
	onRetryLoadMessages?: () => void;
}
