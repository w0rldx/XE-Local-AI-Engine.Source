import type { ChatRole } from "@/features/chat/models/ChatModels";
import type { ContextWindowSnapshot } from "@/features/chat/models/ContextWindowModels";

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

		if (
			message.inputTokens !== null &&
			message.inputTokens !== undefined &&
			message.outputTokens !== null &&
			message.outputTokens !== undefined
		) {
			return message.inputTokens + message.outputTokens;
		}
	}

	return undefined;
}

// The newest assistant snapshot, walking back past a turn still streaming (it has none yet), the same way the used
// count walks past a turn without usage, so the popover and the badge describe the same round.
export function deriveContextWindow(
	messages: readonly { role: ChatRole | string; contextWindow?: ContextWindowSnapshot | null }[],
): ContextWindowSnapshot | undefined {
	for (const message of messages.toReversed()) {
		if (isAssistantRole(message.role) && message.contextWindow) {
			return message.contextWindow;
		}
	}

	return undefined;
}

function isAssistantRole(role: ChatRole | string): boolean {
	return typeof role === "string" && role.toLowerCase() === "assistant";
}
