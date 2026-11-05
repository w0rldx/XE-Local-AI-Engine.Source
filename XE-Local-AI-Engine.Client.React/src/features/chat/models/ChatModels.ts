import type { ReactNode } from "react";

import type { AgentOption } from "@/core/agents/models/AgentOption";
import type { XeLocalAiEngineClientServicesKnowledgeKnowledgeScoreKind } from "@/core/api/generated";
import type { ChatUiCapabilities } from "@/core/chat/models/ChatCapabilityGates";
import type { ModelOption } from "@/core/models/ModelOption";
// Re-exported so chat's own call sites keep importing from here; the type itself lives in core because `agents` also
// depends on it (see ReasoningEffort.ts for the effort-level doc comment).
import type { ReasoningEffort } from "@/core/models/ReasoningEffort";
import type { ChatCommandOption } from "@/features/chat/models/SlashCommandModels";

export type { ReasoningEffort };

import type { ChatMessagePart, ToolCallState } from "@/core/models/ChatMessageParts";
import type { ChatAttachment, PendingAttachmentUpload } from "@/features/chat/models/ChatAttachmentModels";
import type { ContextWindowSnapshot } from "@/features/chat/models/ContextWindowModels";
import type { ChatTurnThroughput } from "@/features/chat/models/NodeChatStreamTypes";

export type ChatRole = "user" | "assistant" | "system" | "tool";

export type MessageStatus = "pending" | "queued" | "streaming" | "completed" | "cancelled" | "failed" | "interrupted";

export type ChatOrigin = "local" | "remote";

/**
 * One knowledge-base excerpt that grounded a plain-chat assistant turn. Rendered in the collapsible
 * "Sources" strip under the answer. Carries only non-sensitive provenance (ids, derived title/section, score + kind);
 * no chunk body text rides here. Maps from the persisted metadata blob via NodeChatMapper.
 */
export interface ChatMessageSource {
	documentId: string;
	chunkId: string;
	title: string;
	section?: string;
	score: number;
	// Which scale `score` is on; undefined for sources persisted before the kind was recorded.
	scoreKind?: XeLocalAiEngineClientServicesKnowledgeKnowledgeScoreKind;
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
	// The server `FailureCategory` name of a failed turn; drives the localized headline above `error`.
	failureCategory?: string;
	origin?: ChatOrigin;
	inputTokens?: number;
	outputTokens?: number;
	totalTokens?: number;
	reasoningTokens?: number;
	// Context-window snapshot of the turn's last provider round; absent for user, legacy and pre-feature rows.
	contextWindow?: ContextWindowSnapshot;
	// Time to first token and prompt/generation throughput of an assistant turn; absent for user, legacy and cloud rows
	// that reported none. Rendered as one muted line in Advanced UI mode only.
	throughput?: ChatTurnThroughput;
	parentMessageId?: string;
	variantGroupId?: string;
	// Ordered interleave (reasoning → tool → reasoning → …) for an assistant turn. Drives the in-order render of
	// the reasoning/tool region; the trailing answer still renders from `content`. Absent for user/legacy turns.
	parts?: ChatMessagePart[];
	// Live only, while a turn with tool calls streams: the whole streamed text in the wire's global offset space.
	// `content` and the `text` parts are re-derived from it at the tools' `contentOffset`s; cleared at the terminal.
	streamedContent?: string;
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

/** Prev/next navigation across the sibling revisions (variant group) of an assistant turn. */
export interface ChatMessageRevisionNav {
	activeIndex: number;
	total: number;
	onPrevious: () => void;
	onNext: () => void;
}

/** Which action slots are active for a given chat turn. Grouped to avoid a sprawl of individual boolean props. */
export interface ChatMessageActionCapabilities {
	copy: boolean;
	regenerate: boolean;
	branch: boolean;
	revisionNav: boolean;
	feedback: boolean;
	menu: boolean;
	showTokensPerSecond: boolean;
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
	// Plain mode: turns run with no agent, persona, tools, knowledge base or memory, and send plainSystemPrompt verbatim
	// (or no system prompt when it is unset).
	isPlain?: boolean;
	plainSystemPrompt?: string;
	// The agent the conversation is bound to; a send that picks no agent of its own runs as this one.
	agentDefinitionId?: string;
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
	// Server-stamped ISO-8601 UTC time of the runtime-phase transition, carried on the same assistant-phase
	// event. Anchors the cold-load elapsed timer so a reload mid-load resumes the count instead of restarting.
	runtimePhaseChangedAtUtc?: string;
	// Why the loading phase loads the model and how long its previous load took, off the same assistant-phase event.
	runtimePhaseReason?: string;
	runtimePhaseLastLoadMs?: number;
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

export interface ContextUsageModel {
	usedTokens?: number;
	maxTokens?: number;
	isAuthoritative: boolean;
	modelLabel: string;
	nodeLabel: string;
	// Display name of the agent the request belongs to, when agent mode applies.
	agentLabel?: string;
	// The last provider round's snapshot from the newest assistant message; undefined before the first response.
	contextWindow?: ContextWindowSnapshot;
}

export interface ChatInputStatus {
	isSending: boolean;
	chatInputDisabled?: boolean;
	modelSelectorDisabled?: boolean;
	sendDisabled?: boolean;
	/** Renders the agent picker read-only. Set by an owner that pins the agent (see {@link ChatScope}). */
	agentSelectorDisabled?: boolean;
}

// The conversation-list fetch, envelope and all: the list itself plus the node-level chat facts the endpoint reports
// beside it. It is the chat page's unconditional first GET, which is why the composer's message-size limit rides it
// (see ComposerSizeLimit). `maxMessageSizeKb` is absent when the node reports none; the composer then runs no
// pre-check and an oversized send is rejected by the hub exactly as before.
export interface ChatConversationListModel {
	conversations: ChatConversationModel[];
	maxMessageSizeKb?: number;
	// The node's Security:MaxUploadFileSizeMb, for the attachment pre-check; absent → no pre-check.
	maxUploadFileSizeMb?: number;
}

// Result of a manual, non-destructive compaction. `outcome` mirrors the backend ConversationCompactionOutcome names
// ("Compacted", "NothingToCompact", "NoLocalModel", "SummarizerReturnedNothing", "TimedOut", "ConversationNotFound"); the remaining
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
	// Whether the active model advertises the `tools` capability. Gated together with the node-wide
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
	// The sidebar holds only the most recent page; it then offers "Show all" through onShowAllConversations.
	conversationListTruncated?: boolean;
	onShowAllConversations?: () => void;
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
	// The open conversation is bound to an agent, which a send without a pick runs as: the Default Assistant row cannot apply.
	conversationAgentBound?: boolean;
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
	// Set the selected conversation's plain mode and custom system prompt; renders the plain controls when wired.
	onSetConversationPlain?: (conversationId: string, isPlain: boolean, plainSystemPrompt: string | undefined) => void;
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
	// Chat workflow mode (features/chat/workflow). Absent when Graph Workflows is off or the chat is scoped, and then
	// every surface below renders exactly as it did before workflow mode existed.
	workflow?: ChatWorkflowSlots;
}

/** What workflow mode adds to the chat surfaces. Every member is optional; an absent one changes nothing. */
export interface ChatWorkflowSlots {
	/** The workflow picker, rendered beside the agent picker in the composer toolbar. */
	readonly selector?: ReactNode;
	/** Status card, input-request banner and send refusals, rendered between the message list and the composer. */
	readonly composerHeader?: ReactNode;
	readonly composerPlaceholder?: string;
	/** The bound run is busy: the composer is read-only (the conversation list stays usable). */
	readonly composerDisabled?: boolean;
	/** A workflow message is on its way: Send and Enter are refused, the draft stays editable. */
	readonly sendDisabled?: boolean;
	/** Set when the attachment chips must not be sent; the chips are disabled and this hint explains why. */
	readonly attachmentsDisabledHint?: string;
	/** Rendered right after a message row: the activity of the runs that message triggered. */
	readonly renderAfterMessage?: (messageId: string) => ReactNode;
}
