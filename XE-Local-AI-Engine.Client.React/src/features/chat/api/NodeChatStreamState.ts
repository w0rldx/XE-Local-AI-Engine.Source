import type { NodeChatStreamEventDto } from "@/features/chat/api/NodeChatApi";
import type { ChatConversationModel, ChatMessageModel, ChatStreamingState, MessageStatus } from "@/features/chat/models/ChatModels";

export const nodeChatStreamEventTypes = {
	userMessagePersisted: "user-message-persisted",
	assistantPending: "assistant-pending",
	assistantStreaming: "assistant-streaming",
	assistantDelta: "assistant-delta",
	assistantCompleted: "assistant-completed",
	assistantCancelled: "assistant-cancelled",
	assistantFailed: "assistant-failed",
	assistantInterrupted: "assistant-interrupted",
} as const;

const knownStatuses = new Set<MessageStatus>(["pending", "streaming", "completed", "cancelled", "failed", "interrupted"]);

export interface OptimisticNodeChatSendIds {
	userMessageId: string;
	assistantMessageId: string;
	requestId: string;
}

export interface AppliedNodeChatStreamEvent {
	conversation: ChatConversationModel;
	streamingMessage: ChatStreamingState;
	isTerminal: boolean;
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
				isActive: true,
			},
			isTerminal: false,
		};
	}

	const existing = conversation.messages.find((message) => message.id === event.messageId && message.role === "assistant");
	const fallbackStatus: MessageStatus = terminalStatus ?? (event.type === nodeChatStreamEventTypes.assistantPending ? "pending" : "streaming");
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
			isActive: !isTerminal,
			error: event.error ?? undefined,
		},
		isTerminal,
	};
}

export function markNodeChatStreamTerminated(
	conversation: ChatConversationModel,
	messageId: string,
	status: Extract<MessageStatus, "cancelled" | "failed" | "interrupted">,
	error?: string,
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
			isActive: false,
			error,
		},
		isTerminal: true,
	};
}
