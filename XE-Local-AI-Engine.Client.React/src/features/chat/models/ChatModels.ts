import type { ReactNode } from "react";

export type ChatRole = "user" | "assistant" | "system" | "tool";

export type MessageStatus = "pending" | "queued" | "streaming" | "completed" | "cancelled" | "failed" | "interrupted";

export type ChatOrigin = "local" | "remote";

// "none"/"low"/"medium"/"high" are the graded efforts for models with the Ollama `thinking` capability.
// "on" is the binary-reasoning ON state for a model WITHOUT that capability that still reasons by default
// (e.g. some GGUF chat templates): it maps to "omit the think field" so the model's built-in reasoning runs,
// while "none" maps to think:false (suppress). Graded models never use "on"; binary models only use "on"/"none".
export type ReasoningEffort = "none" | "on" | "low" | "medium" | "high";

export type ToolCallState = "requesting" | "waiting" | "received" | "failed";

export type ChatMessagePartKind = "reasoning" | "tool" | "text";

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

export type ChatMessagePart = ChatReasoningPart | ChatToolPart | ChatTextPart;

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
	// Agent attribution fields stamped at send time (ride metadata_json blob, no migration — §6 of the plan).
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
	// Carried from the tool-call event so the rendered tool call can surface the approval requirement; beta
	// ships only auto-execute tools, so this is currently always false.
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
	// Tool-level metadata: whether the tool requires explicit approval before executing. Carried so a future
	// tools-overview UI can surface/toggle it. Beta ships only auto-execute tools, so no approval dialog yet.
	requiresApproval?: boolean;
}

export interface ModelOption {
	value: string;
	label: string;
	displayName?: string;
	isReasoningModel?: boolean;
	// Whether the model advertises the Ollama `tools` capability. Drives whether the composer offers the
	// local-tool controls (gated together with the node-wide capability). Undefined on the local-default
	// option (the runtime picks a concrete model later), so callers treat undefined as "not tool-capable".
	isToolCapable?: boolean;
	isAvailable: boolean;
	statusLabel?: string;
	// True for cloud-provider models (e.g. Codex/OpenAI). Drives the "Cloud (Codex)" section in the picker
	// and the egress cue badge. Local models never set this; absence is equivalent to false.
	isCloud?: boolean;
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
}

// Shared agent option type used by Chat.tsx (derivation), ChatDisplayShellProps, ChatInputArea, and AgentSelectorCard.
// The single derivation site is Chat.tsx; all downstream components receive it as a prop.
export interface AgentOption {
	readonly id: string;
	readonly name: string;
	readonly description: string;
	readonly kind: "Single" | "Orchestrator";
	readonly modelProfile: string | null;
}

// Matches the backend AgentDefaults.DefaultAgentName seeded slug. Used to exclude the Default Assistant from
// the agent picker so the user never selects it explicitly (mode-off reproduces it transparently).
// Note: a provenance-based filter (slug or source field) is a deferred follow-up; for now the name comparison
// is the single exclusion site.
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
	contextUsage?: ContextUsageModel;
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
	agentControlsAvailable?: boolean;
	agentModeEnabled?: boolean;
	selectedAgentId?: string;
	agentOptions?: readonly AgentOption[];
	// Single merged agent control: "" => Default Assistant (agent mode off); any other id => enable mode + stamp it.
	onSelectAgent?: (agentId: string) => void;
	onSend: (content: string, effort: ReasoningEffort, model: string) => void;
	onCancel: () => void;
	onRegenerate?: (messageId: string) => void;
	onConversationSearchChange?: (query: string) => void;
	onToggleShowArchivedConversations?: (showArchived: boolean) => void;
	onRenameConversation?: (conversationId: string, title: string) => void;
	onToggleConversationPinned?: (conversationId: string, isPinned: boolean) => void;
	onToggleConversationArchived?: (conversationId: string, archived: boolean) => void;
	onDeleteConversation?: (conversationId: string, skipConfirm: boolean) => void;
	onBranchFromMessage?: (messageId: string) => void;
	activeRevisionByGroup?: Readonly<Record<string, string>>;
	onSelectRevision?: (variantGroupId: string, messageId: string) => void;
	feedbackByMessageId?: Readonly<Record<string, ChatMessageFeedback>>;
	pendingFeedbackMessageId?: string;
	onSubmitFeedback?: (messageId: string, rating: ChatFeedbackRating, comment: string | undefined) => void;
	conversationListCollapsed?: boolean;
	disabledNotice?: ReactNode;
	// True while the selected conversation's full payload (with messages) is loading. Forwarded to the
	// message list so the empty-state never flashes over a populated thread during the refetch.
	isLoadingMessages?: boolean;
}
