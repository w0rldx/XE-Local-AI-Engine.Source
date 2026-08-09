import type { QueryClient } from "@tanstack/react-query";

import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse as LocalModelResponse } from "@/core/api/generated/types.gen";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";
import { isLocalChatModel } from "@/features/chat/pages/ChatModelOptions";
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";

// A model counts as chat-capable only when its kind is exactly "Chat" — a whitelist matching the chat picker
// (toChatModelOptions), which likewise offers `kind === "Chat"` and excludes every non-chat kind (Embedding, Reranker,
// Unknown, and any future kind). A blacklist such as `!== "Embedding"` would silently treat a reranker-only or
// future-kind install as chat-capable and falsely advance the tour. Deriving from the same list and rule the picker
// uses keeps the tour's notion of "installed chat model" in lockstep with what the user can actually select.
function isChatCapable(model: LocalModelResponse | undefined): boolean {
	if (!model?.modelName) {
		return false;
	}
	return isLocalChatModel(model);
}

// True once at least one chat-capable model is actually installed/selectable. Drives the install step's advance —
// the tour never moves past install on a timer, only on this real state flipping.
export function hasInstalledChatModel(items: readonly LocalModelResponse[] | undefined): boolean {
	return (items ?? []).some(isChatCapable);
}

// The default-confirm step advances when a chat-capable model is the resolved default. `selectedModelName` is the
// runtime's effective default; we confirm it names a chat-capable installed model.
export function hasChatCapableDefault(
	items: readonly LocalModelResponse[] | undefined,
	selectedModelName: string | null | undefined,
): boolean {
	if (!selectedModelName) {
		return false;
	}
	const selected = (items ?? []).find((model) => model.modelName === selectedModelName);
	return isChatCapable(selected);
}

// Counts cached non-empty assistant messages so the provider can baseline existing history and react only when a new
// reply appears. Scanning the conversation query cache (rather than a specific conversation id) keeps the provider
// decoupled from the router, which it lives outside of.
export function countVisibleAssistantReplies(queryClient: QueryClient): number {
	const conversations = queryClient.getQueriesData<ChatConversationModel>({
		queryKey: nodeChatQueryKeys.conversations(),
	});

	return conversations.reduce(
		(total, [, conversation]) =>
			total +
			(conversation?.messages ?? []).filter(
				(message) => message.role === "assistant" && message.content.trim().length > 0,
			).length,
		0,
	);
}

export function hasVisibleAssistantReply(queryClient: QueryClient): boolean {
	return countVisibleAssistantReplies(queryClient) > 0;
}
