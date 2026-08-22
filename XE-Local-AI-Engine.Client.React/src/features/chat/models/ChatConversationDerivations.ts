import type { ChatConversationModel, MessageStatus } from "@/features/chat/models/ChatModels";

export function mergeSelectedConversation(
	conversations: ChatConversationModel[],
	selectedConversation?: ChatConversationModel,
): ChatConversationModel[] {
	if (!selectedConversation) {
		return conversations;
	}

	const hasSelectedConversation = conversations.some((conversation) => conversation.id === selectedConversation.id);
	if (!hasSelectedConversation) {
		return [selectedConversation, ...conversations];
	}

	return conversations.map((conversation) =>
		conversation.id === selectedConversation.id ? selectedConversation : conversation,
	);
}

const liveAssistantStatuses = new Set<MessageStatus>(["pending", "queued", "streaming"]);

// Pick the persisted in-flight assistant row that a cold-load resume belongs to. A resumed stream's invocation id
// differs from the row id already rendered by the client, so its events must be remapped onto the latest live row.
export function inFlightAssistantMessageId(conversation: ChatConversationModel): string | undefined {
	return conversation.messages.findLast(
		(message) => message.role === "assistant" && liveAssistantStatuses.has(message.status),
	)?.id;
}

export function titleFromContent(content: string): string {
	const normalized = content.replace(/\s+/g, " ").trim();
	return normalized.length > 48 ? `${normalized.slice(0, 45)}…` : normalized || "New conversation";
}

// A regenerate streams its server-minted variant before the authoritative group id is available. Stamp the original
// and streamed sibling into one temporary group so the replacement renders in place until the post-stream refetch.
export function stampVariantGroup(
	conversation: ChatConversationModel,
	originalMessageId: string,
	variantMessageId: string,
	variantGroupId: string,
): ChatConversationModel {
	return {
		...conversation,
		messages: conversation.messages.map((message) => {
			if (message.id === variantMessageId) {
				return { ...message, variantGroupId };
			}
			if (message.id === originalMessageId && !message.variantGroupId) {
				return { ...message, variantGroupId };
			}
			return message;
		}),
	};
}
