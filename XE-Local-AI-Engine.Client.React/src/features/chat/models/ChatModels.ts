import type { ReactNode } from "react";

export type ChatRole = "user" | "assistant" | "system" | "tool";

export type MessageStatus = "pending" | "streaming" | "completed" | "cancelled" | "failed" | "interrupted";

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
	role: ChatRole;
	content: string;
	reasoning?: string;
	status: MessageStatus;
	createdAt: string;
	updatedAt?: string;
	sortOrder: number;
	model?: string;
	error?: string;
	inputTokens?: number;
	outputTokens?: number;
	totalTokens?: number;
	reasoningTokens?: number;
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
	messages: ChatMessageModel[];
}

export interface ChatStreamingState {
	conversationId: string;
	messageId: string;
	content: string;
	reasoning?: string;
	reasoningOverflowBytes?: number;
	isActive: boolean;
	isDelayed?: boolean;
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
	onSelectConversation: (conversationId: string) => void;
	onCreateConversation: () => void;
	onToggleConversationList: () => void;
	onModelChange: (model: string) => void;
	onReasoningEffortChange: (effort: ReasoningEffort) => void;
	onSend: (content: string, effort: ReasoningEffort, model: string) => void;
	onCancel: () => void;
	conversationListCollapsed?: boolean;
	disabledNotice?: ReactNode;
}
