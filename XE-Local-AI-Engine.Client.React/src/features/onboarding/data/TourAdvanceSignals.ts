import type { QueryClient } from "@tanstack/react-query";

import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse as LocalModelResponse } from "@/core/api/generated/types.gen";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";

// A model is usable as the chat default unless it is an embedding-only model. Embedding is the one kind the chat
// picker excludes; "Chat" and "Unknown" are both selectable (the runtime default resolver accepts any installed named
// non-embedding model). Deriving from the same list the chat picker uses keeps the tour's notion of "installed chat
// model" in lockstep with what the user can actually select.
function isChatCapable(model: LocalModelResponse | undefined): boolean {
	if (!model?.modelName) {
		return false;
	}
	return model.kind !== "Embedding";
}

// R1: true once at least one chat-capable model is actually installed/selectable. Drives the install step's advance —
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

// True when any cached conversation carries a non-empty assistant message — the real signal that the user's first send
// produced a streamed reply. Scanning the conversation query cache (rather than a specific conversation id) keeps the
// provider decoupled from the router, which it lives outside of.
export function hasVisibleAssistantReply(queryClient: QueryClient): boolean {
	const conversations = queryClient.getQueriesData<ChatConversationModel>({
		queryKey: nodeChatQueryKeys.conversations(),
	});

	return conversations.some(([, conversation]) =>
		(conversation?.messages ?? []).some((message) => message.role === "assistant" && message.content.trim().length > 0),
	);
}
