import type { ReactNode } from "react";

export type ChatRole = "user" | "assistant" | "system" | "tool";

export type MessageStatus = "pending" | "queued" | "streaming" | "completed" | "cancelled" | "failed" | "interrupted";

export type ChatOrigin = "local" | "remote";

export type ReasoningEffort = "none" | "low" | "medium" | "high";

export type ToolCallState = "requesting" | "waiting" | "received" | "failed";

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
	// Node-local feedback on this assistant turn, carried on the message read DTO (feedback flow). Undefined when
	// no feedback has been recorded; presence drives the feedback control's active state.
	feedbackRating?: ChatFeedbackRating;
	feedbackComment?: string;
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
	isAvailable: boolean;
	statusLabel?: string;
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

export interface ChatUiCapabilities {
	readonly showLocalToolControls: boolean;
	readonly showToolApprovalControls: boolean;
	readonly showConversationFeedbackControls: boolean;
	readonly showEncryptedConversationControls: boolean;
	readonly showClientNodeRoutingControls: boolean;
	readonly showFileAttachmentControls: boolean;
	readonly showImageAttachmentControls: boolean;
}

export interface ChatDisplayShellProps {
	conversations: ChatConversationModel[];
	selectedConversationId: string;
	modelOptions: ModelOption[];
	selectedModel: string;
	reasoningEffort: ReasoningEffort;
	availableReasoningEfforts: ReasoningEffort[];
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
