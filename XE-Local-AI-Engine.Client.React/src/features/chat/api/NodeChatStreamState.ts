import { nodeChatToolStreamEventTypes, type NodeChatStreamEventDto } from "@/features/chat/api/NodeChatApi";
import { mapToolCallEvent } from "@/features/chat/api/NodeChatMapper";
import type { ChatConversationModel, ChatMessageModel, ChatStreamingState, ChatTimelineEntry, MessageStatus } from "@/features/chat/models/ChatModels";

export const nodeChatStreamEventTypes = {
	userMessagePersisted: "user-message-persisted",
	assistantPending: "assistant-pending",
	assistantQueued: "assistant-queued",
	assistantStreaming: "assistant-streaming",
	assistantDelta: "assistant-delta",
	assistantCompleted: "assistant-completed",
	assistantCancelled: "assistant-cancelled",
	assistantFailed: "assistant-failed",
	assistantInterrupted: "assistant-interrupted",
	// Tool lifecycle events (Phase D6) reuse the dedicated tool-event constant so the wire names stay DRY.
	toolCallRequested: nodeChatToolStreamEventTypes.toolCallRequested,
	toolCallCompleted: nodeChatToolStreamEventTypes.toolCallCompleted,
} as const;

function isToolStreamEvent(eventType: string): boolean {
	return eventType === nodeChatStreamEventTypes.toolCallRequested || eventType === nodeChatStreamEventTypes.toolCallCompleted;
}

const knownStatuses = new Set<MessageStatus>(["pending", "queued", "streaming", "completed", "cancelled", "failed", "interrupted"]);

export interface OptimisticNodeChatSendIds {
	userMessageId: string;
	assistantMessageId: string;
	requestId: string;
}

export interface AppliedNodeChatStreamEvent {
	conversation: ChatConversationModel;
	streamingMessage: ChatStreamingState;
	isTerminal: boolean;
	// A tool-lifecycle event yields a timeline entry to accumulate (keyed by tool call id) instead of mutating
	// assistant content; assistant/lifecycle events leave this undefined.
	timelineEntry?: ChatTimelineEntry;
}

/**
 * Wraps the `ChatToolCall` mapped from a tool-lifecycle event into the `ChatTimelineEntry` the render pipeline
 * (`ChatMessage`'s `calls()` helper) consumes — scoped to the streaming assistant message so `ChatMessageList`
 * filters it onto the right turn. The tool call id is the entry id so a `tool-call-completed` collapses onto its
 * matching `tool-call-requested` entry instead of duplicating it.
 */
function toToolTimelineEntry(event: NodeChatStreamEventDto): ChatTimelineEntry | undefined {
	const toolCall = mapToolCallEvent(event);
	if (!toolCall) {
		return undefined;
	}

	return {
		id: toolCall.id,
		messageId: event.messageId,
		invocationId: event.requestId || undefined,
		type: event.type === nodeChatStreamEventTypes.toolCallCompleted ? "ToolResult" : "ToolCall",
		toolName: toolCall.name,
		toolArgs: toolCall.args,
		toolResult: toolCall.result,
		state: toolCall.state,
		requiresApproval: toolCall.requiresApproval,
		createdAt: isoFromUnixMilliseconds(event.occurredAtUtc),
	};
}

/**
 * Accumulates a tool timeline entry per streaming turn: a `tool-call-completed` updates the matching
 * `tool-call-requested` entry (same tool call id) in place rather than appending a duplicate.
 */
export function accumulateToolTimelineEntry(entries: ChatTimelineEntry[], entry: ChatTimelineEntry): ChatTimelineEntry[] {
	const existingIndex = entries.findIndex((candidate) => candidate.id === entry.id);
	if (existingIndex < 0) {
		return [...entries, entry];
	}

	return entries.map((candidate, index) =>
		index === existingIndex
			? {
					...candidate,
					...entry,
					// The completed tool-event omits the approval requirement (it lives only on the requested
					// event), so preserve it from whichever entry defines it instead of letting the spread
					// clobber the flag to undefined.
					requiresApproval: entry.requiresApproval ?? candidate.requiresApproval,
				}
			: candidate,
	);
}

function normalizeStatus(status: string | null | undefined, fallback: MessageStatus): MessageStatus {
	const normalized = status?.toLowerCase() as MessageStatus | undefined;
	return normalized && knownStatuses.has(normalized) ? normalized : fallback;
}

function terminalStatusForEvent(eventType: string): MessageStatus | undefined {
	switch (eventType) {
		case nodeChatStreamEventTypes.assistantCompleted:
			return "completed";
		case nodeChatStreamEventTypes.assistantCancelled:
			return "cancelled";
		case nodeChatStreamEventTypes.assistantFailed:
			return "failed";
		case nodeChatStreamEventTypes.assistantInterrupted:
			return "interrupted";
		default:
			return undefined;
	}
}

function maxSortOrder(messages: ChatMessageModel[]): number {
	return messages.reduce((max, message) => Math.max(max, message.sortOrder), 0);
}

function isoFromUnixMilliseconds(value: number | undefined): string {
	const date = new Date(value ?? Date.now());
	return Number.isNaN(date.getTime()) ? new Date().toISOString() : date.toISOString();
}

function replaceMessage(messages: ChatMessageModel[], nextMessage: ChatMessageModel): ChatMessageModel[] {
	const existingIndex = messages.findIndex((message) => message.id === nextMessage.id);
	if (existingIndex < 0) {
		return [...messages, nextMessage].toSorted((left, right) => left.sortOrder - right.sortOrder || left.createdAt.localeCompare(right.createdAt));
	}

	return messages.map((message, index) => (index === existingIndex ? nextMessage : message));
}

export function appendOptimisticNodeChatSend(
	conversation: ChatConversationModel,
	ids: OptimisticNodeChatSendIds,
	content: string,
	nowIso: string,
	model?: string,
): ChatConversationModel {
	const nextSortOrder = maxSortOrder(conversation.messages) + 1;
	const userMessage: ChatMessageModel = {
		id: ids.userMessageId,
		conversationId: conversation.id,
		role: "user",
		content,
		status: "completed",
		createdAt: nowIso,
		updatedAt: nowIso,
		sortOrder: nextSortOrder,
	};
	const assistantMessage: ChatMessageModel = {
		id: ids.assistantMessageId,
		conversationId: conversation.id,
		role: "assistant",
		content: "",
		status: "pending",
		createdAt: nowIso,
		updatedAt: nowIso,
		sortOrder: nextSortOrder + 1,
		model,
	};

	return {
		...conversation,
		updatedAt: nowIso,
		lastActivity: nowIso,
		lastMessagePreview: content,
		messages: [...conversation.messages, userMessage, assistantMessage],
	};
}

export function applyNodeChatStreamEvent(conversation: ChatConversationModel, event: NodeChatStreamEventDto): AppliedNodeChatStreamEvent {
	const terminalStatus = terminalStatusForEvent(event.type);
	const isTerminal = terminalStatus !== undefined;

	// Tool-lifecycle events feed the activity timeline only — they must NOT mutate assistant content or status.
	// The conversation is returned untouched and the streaming state is re-derived from the in-flight assistant
	// turn so the turn stays live (isActive) while tools run.
	if (isToolStreamEvent(event.type)) {
		const current = conversation.messages.find((message) => message.id === event.messageId && message.role === "assistant");
		return {
			conversation,
			streamingMessage: {
				conversationId: event.conversationId,
				messageId: event.messageId,
				content: current?.content ?? "",
				reasoning: current?.reasoning,
				startedAt: current?.createdAt ?? isoFromUnixMilliseconds(event.occurredAtUtc),
				isActive: true,
				inputTokens: current?.inputTokens,
				outputTokens: current?.outputTokens,
				totalTokens: current?.totalTokens,
				reasoningTokens: current?.reasoningTokens,
			},
			isTerminal: false,
			timelineEntry: toToolTimelineEntry(event),
		};
	}

	// The local stream optimistically inserts the user message using the request's userMessageId.
	// Some backend stream versions report the assistant correlation id on the user-persisted event,
	// so treating that event as an assistant mutation would clobber the placeholder.
	if (event.type === nodeChatStreamEventTypes.userMessagePersisted) {
		const currentAssistant = conversation.messages.find((message) => message.id === event.messageId && message.role === "assistant");
		return {
			conversation,
			streamingMessage: {
				conversationId: event.conversationId,
				messageId: event.messageId,
				content: currentAssistant?.content ?? event.content ?? "",
				reasoning: currentAssistant?.reasoning ?? event.reasoning ?? undefined,
				startedAt: currentAssistant?.createdAt ?? isoFromUnixMilliseconds(event.occurredAtUtc),
				isActive: true,
				inputTokens: currentAssistant?.inputTokens ?? event.inputTokens ?? undefined,
				outputTokens: currentAssistant?.outputTokens ?? event.outputTokens ?? undefined,
				totalTokens: currentAssistant?.totalTokens ?? event.totalTokens ?? undefined,
				reasoningTokens: currentAssistant?.reasoningTokens ?? event.reasoningTokens ?? undefined,
			},
			isTerminal: false,
		};
	}

	const existing = conversation.messages.find((message) => message.id === event.messageId && message.role === "assistant");
	const isQueued = event.type === nodeChatStreamEventTypes.assistantQueued;
	const fallbackStatus: MessageStatus =
		terminalStatus ?? (isQueued ? "queued" : event.type === nodeChatStreamEventTypes.assistantPending ? "pending" : "streaming");
	const status = normalizeStatus(event.status, fallbackStatus);
	const eventTime = isoFromUnixMilliseconds(event.occurredAtUtc);
	const content = event.content ?? `${existing?.content ?? ""}${event.delta ?? ""}`;
	const reasoning = event.reasoning ?? (event.reasoningDelta ? `${existing?.reasoning ?? ""}${event.reasoningDelta}` : existing?.reasoning);
	const assistantMessage: ChatMessageModel = {
		id: event.messageId,
		conversationId: event.conversationId,
		role: "assistant",
		content,
		reasoning: reasoning ?? undefined,
		status,
		createdAt: existing?.createdAt ?? eventTime,
		updatedAt: eventTime,
		sortOrder: existing?.sortOrder ?? maxSortOrder(conversation.messages) + 1,
		model: event.model ?? existing?.model,
		error: event.error ?? undefined,
		inputTokens: event.inputTokens ?? existing?.inputTokens,
		outputTokens: event.outputTokens ?? existing?.outputTokens,
		totalTokens: event.totalTokens ?? existing?.totalTokens,
		reasoningTokens: event.reasoningTokens ?? existing?.reasoningTokens,
	};
	const nextConversation: ChatConversationModel = {
		...conversation,
		updatedAt: eventTime,
		lastActivity: eventTime,
		lastMessagePreview: content || conversation.lastMessagePreview,
		messages: replaceMessage(conversation.messages, assistantMessage),
	};

	return {
		conversation: nextConversation,
		streamingMessage: {
			conversationId: event.conversationId,
			messageId: event.messageId,
			content,
			reasoning: reasoning ?? undefined,
			startedAt: assistantMessage.createdAt,
			isActive: !isTerminal,
			// Queued turns are live (isActive) but not yet streaming; clear once the streaming event arrives.
			isQueued: status === "queued",
			error: event.error ?? undefined,
			inputTokens: assistantMessage.inputTokens,
			outputTokens: assistantMessage.outputTokens,
			totalTokens: assistantMessage.totalTokens,
			reasoningTokens: assistantMessage.reasoningTokens,
		},
		isTerminal,
	};
}

export function markNodeChatStreamTerminated(
	conversation: ChatConversationModel,
	messageId: string,
	status: Extract<MessageStatus, "cancelled" | "failed" | "interrupted">,
	error?: string,
	failureCategory?: string,
): AppliedNodeChatStreamEvent {
	const nowIso = new Date().toISOString();
	const existing = conversation.messages.find((message) => message.id === messageId && message.role === "assistant");
	const assistantMessage: ChatMessageModel = {
		id: messageId,
		conversationId: conversation.id,
		role: "assistant",
		content: existing?.content ?? "",
		reasoning: existing?.reasoning,
		status,
		createdAt: existing?.createdAt ?? nowIso,
		updatedAt: nowIso,
		sortOrder: existing?.sortOrder ?? maxSortOrder(conversation.messages) + 1,
		model: existing?.model,
		error,
		inputTokens: existing?.inputTokens,
		outputTokens: existing?.outputTokens,
		totalTokens: existing?.totalTokens,
		reasoningTokens: existing?.reasoningTokens,
	};
	const nextConversation = {
		...conversation,
		updatedAt: nowIso,
		lastActivity: nowIso,
		messages: replaceMessage(conversation.messages, assistantMessage),
	};

	return {
		conversation: nextConversation,
		streamingMessage: {
			conversationId: conversation.id,
			messageId,
			content: assistantMessage.content,
			reasoning: assistantMessage.reasoning,
			startedAt: assistantMessage.createdAt,
			isActive: false,
			error,
			failureCategory,
			inputTokens: assistantMessage.inputTokens,
			outputTokens: assistantMessage.outputTokens,
			totalTokens: assistantMessage.totalTokens,
			reasoningTokens: assistantMessage.reasoningTokens,
		},
		isTerminal: true,
	};
}
