import type {
	NodeChatConversationResponseDto,
	NodeChatConversationSummaryResponseDto,
	NodeChatMessageResponseDto,
} from "@/features/chat/api/NodeChatApi";
import type { ChatConversationModel, ChatMessageModel, ChatRole, MessageStatus } from "@/features/chat/models/ChatModels";

const knownRoles = new Set<ChatRole>(["user", "assistant", "system", "tool"]);
const knownStatuses = new Set<MessageStatus>(["pending", "streaming", "completed", "cancelled", "failed", "interrupted"]);

function toIso(unixMilliseconds: number): string {
	const date = new Date(unixMilliseconds);
	return Number.isNaN(date.getTime()) ? new Date(0).toISOString() : date.toISOString();
}

function toRole(role: string): ChatRole {
	const normalized = role.toLowerCase() as ChatRole;
	return knownRoles.has(normalized) ? normalized : "assistant";
}

function toStatus(status: string): MessageStatus {
	const normalized = status.toLowerCase() as MessageStatus;
	return knownStatuses.has(normalized) ? normalized : "completed";
}

function titleOrFallback(title: string | null | undefined): string {
	return title?.trim() || "Untitled conversation";
}

export function mapMessage(dto: NodeChatMessageResponseDto): ChatMessageModel {
	return {
		id: dto.messageId,
		conversationId: dto.conversationId,
		role: toRole(dto.role),
		content: dto.content,
		reasoning: dto.reasoning ?? undefined,
		status: toStatus(dto.status),
		createdAt: toIso(dto.createdAtUtc),
		updatedAt: toIso(dto.updatedAtUtc),
		sortOrder: dto.sequence,
		model: dto.model ?? undefined,
		error: dto.error ?? undefined,
		inputTokens: dto.inputTokens ?? undefined,
		outputTokens: dto.outputTokens ?? undefined,
		totalTokens: dto.totalTokens ?? undefined,
		reasoningTokens: dto.reasoningTokens ?? undefined,
	};
}

export function mapConversationSummary(dto: NodeChatConversationSummaryResponseDto): ChatConversationModel {
	const lastSeen = toIso(dto.lastSeenUtc);

	return {
		id: dto.conversationId,
		title: titleOrFallback(dto.title),
		createdAt: toIso(dto.createdAtUtc),
		updatedAt: lastSeen,
		lastActivity: lastSeen,
		lastMessagePreview: dto.lastMessagePreview ?? undefined,
		isArchived: dto.purged,
		messages: [],
	};
}

export function mapConversation(dto: NodeChatConversationResponseDto): ChatConversationModel {
	const lastSeen = toIso(dto.lastSeenUtc);

	return {
		id: dto.conversationId,
		title: titleOrFallback(dto.title),
		createdAt: toIso(dto.createdAtUtc),
		updatedAt: lastSeen,
		lastActivity: lastSeen,
		isArchived: dto.purged,
		messages: dto.messages.map(mapMessage),
	};
}
