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
	createdAt: string;
}

export interface ChatToolCall {
	id: string;
	name: string;
	state: ToolCallState;
	args?: string;
	result?: string;
	duration?: string;
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
	onSend: (content: string, effort: ReasoningEffort, model: string) => void;
	onCancel: () => void;
	onRegenerate?: (messageId: string) => void;
	onConversationSearchChange?: (query: string) => void;
	onToggleShowArchivedConversations?: (showArchived: boolean) => void;
	onRenameConversation?: (conversationId: string, title: string) => void;
	onToggleConversationPinned?: (conversationId: string, isPinned: boolean) => void;
	onToggleConversationArchived?: (conversationId: string, archived: boolean) => void;
	onBranchFromMessage?: (messageId: string) => void;
	activeRevisionByGroup?: Readonly<Record<string, string>>;
	onSelectRevision?: (variantGroupId: string, messageId: string) => void;
	feedbackByMessageId?: Readonly<Record<string, ChatMessageFeedback>>;
	pendingFeedbackMessageId?: string;
	onSubmitFeedback?: (messageId: string, rating: ChatFeedbackRating, comment: string | undefined) => void;
	conversationListCollapsed?: boolean;
	disabledNotice?: ReactNode;
}
