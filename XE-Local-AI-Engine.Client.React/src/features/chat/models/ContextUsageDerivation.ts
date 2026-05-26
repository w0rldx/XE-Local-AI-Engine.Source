import type { ChatRole } from "@/features/chat/models/ChatModels";

interface ContextUsageMessageTokenFields {
	role: ChatRole | string;
	inputTokens?: number | null;
	outputTokens?: number | null;
	totalTokens?: number | null;
}

export function deriveUsedContextTokens(messages: readonly ContextUsageMessageTokenFields[]): number | undefined {
	for (const message of messages.toReversed()) {
		if (!isAssistantRole(message.role)) {
			continue;
		}

		if (message.totalTokens !== null && message.totalTokens !== undefined) {
			return message.totalTokens;
		}

		if (message.inputTokens !== null && message.inputTokens !== undefined && message.outputTokens !== null && message.outputTokens !== undefined) {
			return message.inputTokens + message.outputTokens;
		}
	}

	return undefined;
}

function isAssistantRole(role: ChatRole | string): boolean {
	return typeof role === "string" && role.toLowerCase() === "assistant";
}
