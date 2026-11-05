import { useQueryClient } from "@tanstack/react-query";
import { useCallback } from "react";

import { nodeChatQueryKeys } from "@/core/chat/queries/NodeChatQueryKeys";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { mergeSelectedConversation, titleFromContent } from "@/features/chat/models/ChatConversationDerivations";
import type { ChatConversationListModel, ChatConversationModel } from "@/features/chat/models/ChatModels";
import { adoptWebContentAutoAcceptDraft } from "@/features/chat/stores/WebContentAutoAcceptStore";

// Fallback for the cache updaters when they run before the list query has landed: an empty list with no known
// message-size limit (which simply means the composer runs no size pre-check until the real fetch arrives).
export const emptyConversationList: ChatConversationListModel = { conversations: [] };

interface ChatConversationCacheInput {
	// The list cache entry currently on screen; optimistic writes target this exact variant.
	conversationListQueryKey: ReturnType<typeof nodeChatQueryKeys.conversationList>;
	selectedConversationId: string;
	selectedConversationData: ChatConversationModel | undefined;
	selectedConversationIsPlaceholderData: boolean;
	displayConversations: ChatConversationModel[];
	setRequestedConversationId: (conversationId: string) => void;
}

/** The query-cache writers a turn and a conversation mutation share, and the create-or-load a send targets. */
export function useChatConversationCache({
	conversationListQueryKey,
	selectedConversationId,
	selectedConversationData,
	selectedConversationIsPlaceholderData,
	displayConversations,
	setRequestedConversationId,
}: ChatConversationCacheInput) {
	const queryClient = useQueryClient();

	// Write only the selected-conversation detail cache. Used for per-frame streaming commits: the sidebar has
	// nothing to show for an in-flight turn, so folding the growing conversation into the whole list every frame
	// (mergeSelectedConversation is O(list)) is wasted work. The list is refreshed on terminal events instead.
	const cacheConversationDetail = useCallback(
		(conversation: ChatConversationModel): void => {
			queryClient.setQueryData(nodeChatQueryKeys.conversation(conversation.id), conversation);
		},
		[queryClient],
	);

	const cacheConversation = useCallback(
		(conversation: ChatConversationModel): void => {
			cacheConversationDetail(conversation);
			queryClient.setQueryData<ChatConversationListModel>(conversationListQueryKey, (current = emptyConversationList) => ({
				...current,
				conversations: mergeSelectedConversation(current.conversations, conversation),
			}));
		},
		[cacheConversationDetail, queryClient, conversationListQueryKey],
	);

	const resolveSendConversation = useCallback(
		async (content: string): Promise<ChatConversationModel> => {
			// Only trust the cached selected-conversation payload when it actually belongs to the CURRENT
			// selection AND is not a keepPreviousData placeholder from the thread we just switched away from.
			// A fast switch to an uncached conversation followed by Enter would otherwise resolve
			// the PREVIOUS conversation's payload here and send the turn to the wrong id. When it's stale/
			// placeholder, fall through to the load-by-id path, which fetches the currently-selected thread.
			if (
				selectedConversationData &&
				selectedConversationData.id === selectedConversationId &&
				!selectedConversationIsPlaceholderData
			) {
				return selectedConversationData;
			}

			const summaryConversation = displayConversations.find((conversation) => conversation.id === selectedConversationId);
			if (summaryConversation) {
				const loaded = await nodeChatAdapter.getConversation(summaryConversation.id);
				cacheConversation(loaded);
				return loaded;
			}

			const created = await nodeChatAdapter.createConversation({ title: titleFromContent(content) });
			// The draft's web content choice belongs to the conversation it just became.
			adoptWebContentAutoAcceptDraft(created.id);
			cacheConversation(created);
			setRequestedConversationId(created.id);
			return created;
		},
		[
			cacheConversation,
			displayConversations,
			selectedConversationId,
			selectedConversationData,
			selectedConversationIsPlaceholderData,
			setRequestedConversationId,
		],
	);

	const refreshConversation = useCallback(
		async (conversationId: string): Promise<void> => {
			// Scoped tightly — this runs after EVERY turn. `exact` keeps the conversation's `files` child out (a turn
			// never changes the attachment list; upload/delete invalidate their own key), and `conversationLists`
			// refreshes the sidebar without the broad `conversations()` prefix that would re-invalidate this same
			// detail mid-refetch (the observed duplicate round per turn) plus every other cached conversation.
			await Promise.all([
				queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversation(conversationId), exact: true }),
				queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversationLists() }),
				// The local runtime only fills EffectiveContextTokens once the model is WARM, but the
				// model-details query fires pre-warm — so the context-usage meter would stay pinned to the model's
				// train ceiling (e.g. 262k) even though the server launched with a far smaller `-c` (e.g. 16k). Once
				// a turn reaches a terminal state the model is warm, so invalidate the details query to re-read the
				// real window. Partial-object match on the hey-api single-element key so it invalidates every model
				// path (never `.slice()` a hey-api key).
				queryClient.invalidateQueries({ queryKey: [{ _id: "getLocalModelDetails" }] }),
			]);
		},
		[queryClient],
	);

	return { cacheConversationDetail, cacheConversation, resolveSendConversation, refreshConversation };
}
