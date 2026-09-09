import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useCallback, useMemo, useState } from "react";

import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { mergeSelectedConversation } from "@/features/chat/models/ChatConversationDerivations";
import type { ChatConversationModel, ChatMessageFeedback, ChatScope } from "@/features/chat/models/ChatModels";
import { deriveUsedContextTokens } from "@/features/chat/models/ContextUsageDerivation";
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";

const emptyConversations: ChatConversationModel[] = [];

interface ChatConversationSelectionInput {
	scope?: ChatScope;
	// The operator's remembered `/chat` thread (ignored under scope, which pins its own conversation).
	requestedConversationId: string;
}

interface ChatConversationSelection {
	conversations: ChatConversationModel[];
	conversationsIsLoading: boolean;
	conversationsIsError: boolean;
	conversationsError: unknown;
	// The node's effective Security:MaxMessageSizeKb, reported by the conversation-list endpoint.
	maxMessageSizeKb?: number;
	showArchivedConversations: boolean;
	setShowArchivedConversations: (showArchived: boolean) => void;
	selectedConversationId: string;
	selectedConversationData?: ChatConversationModel;
	selectedConversationIsLoading: boolean;
	selectedConversationIsPlaceholderData: boolean;
	selectedConversationError: unknown;
	selectedConversationLoadFailed: boolean;
	isLoadingSelectedConversation: boolean;
	handleRetryLoadMessages: () => void;
	displayConversations: ChatConversationModel[];
	activeConversation?: ChatConversationModel;
	isRemoteConversation: boolean;
	feedbackByMessageId: Record<string, ChatMessageFeedback>;
	usedContextTokens?: number;
}

/** Which conversation is on screen, its full payload, and everything derived from that payload. */
export function useChatConversationSelection({
	scope,
	requestedConversationId,
}: ChatConversationSelectionInput): ChatConversationSelection {
	const [showArchivedConversations, setShowArchivedConversations] = useState(false);
	const {
		data: conversationsData,
		isLoading: conversationsIsLoading,
		isError: conversationsIsError,
		error: conversationsError,
	} = useQuery({
		queryKey: nodeChatQueryKeys.conversationList(showArchivedConversations),
		queryFn: ({ signal }) => nodeChatAdapter.listConversations({ includeArchived: showArchivedConversations, signal }),
	});

	const conversations = conversationsData?.conversations ?? emptyConversations;
	// The node's effective Security:MaxMessageSizeKb, reported by the conversation-list endpoint. Undefined until that
	// first fetch lands (or on a node that omits it) — the composer then skips its pre-check and the hub enforces.
	const maxMessageSizeKb = conversationsData?.maxMessageSizeKb;
	const requestedConversationExists = conversations.some((conversation) => conversation.id === requestedConversationId);
	// mergeSelectedConversation prepends the pinned conversation when the list does not contain it, so a scoped id
	// renders correctly whatever the (still-fetched — it carries maxMessageSizeKb) conversation list returns.
	const selectedConversationId =
		scope?.conversationId ?? (requestedConversationExists ? requestedConversationId : (conversations[0]?.id ?? ""));

	const {
		data: selectedConversationData,
		isLoading: selectedConversationIsLoading,
		isFetching: selectedConversationIsFetching,
		isPlaceholderData: selectedConversationIsPlaceholderData,
		isError: selectedConversationIsError,
		error: selectedConversationError,
		refetch: refetchSelectedConversation,
	} = useQuery({
		queryKey: nodeChatQueryKeys.conversation(selectedConversationId),
		queryFn: ({ signal }) => nodeChatAdapter.getConversation(selectedConversationId, { signal }),
		enabled: selectedConversationId.length > 0,
		// Keep the prior conversation's full payload mounted while the newly selected one loads so the message
		// list never collapses to the summary entry (no messages) and flashes the empty-state mid-switch.
		placeholderData: keepPreviousData,
	});
	// The selected conversation's full payload failed to load AND we don't already hold its payload (a background
	// refetch failing over good data must NOT blow away the thread — that stays showing the cached messages). The
	// query is keyed by selectedConversationId, so `isError` reflects the CURRENT selection; switching threads
	// re-keys the query and clears this. Drives the inline error+retry state in the message list. Without it, a
	// permanently-failing getConversation left the loading term below true forever (spinner deadlock, no error).
	const selectedConversationLoadFailed =
		selectedConversationId.length > 0 && selectedConversationIsError && selectedConversationData?.id !== selectedConversationId;
	// The full payload (with messages) hasn't settled for the currently selected conversation yet: either the
	// first load, a switch where keepPreviousData is still showing the prior thread (isPlaceholderData), or a
	// background refetch over a cached message-less entry. isFetching is the key signal — isLoading alone is
	// false whenever ANY cached/placeholder data exists for the id, which let the empty-state flash mid-fetch.
	// The failure state takes precedence: once the load has errored we surface the error+retry, not a spinner
	// that would otherwise spin forever (the id-mismatch term below never clears on a permanent failure).
	const isLoadingSelectedConversation =
		!selectedConversationLoadFailed &&
		selectedConversationId.length > 0 &&
		(selectedConversationIsLoading ||
			selectedConversationIsFetching ||
			selectedConversationIsPlaceholderData ||
			selectedConversationData?.id !== selectedConversationId);
	const handleRetryLoadMessages = useCallback(() => {
		// Fire-and-forget refetch: any failure re-lands in the query's own isError state (which drives this same
		// error surface), so there is nothing extra to handle here — mirror the adapter fire-and-forget convention.
		refetchSelectedConversation().catch(() => undefined);
	}, [refetchSelectedConversation]);

	// Gate the selected conversation against the current selection before merging it into the displayed list.
	// When the last conversation is deleted, selectedConversationId becomes "" and the query disables — but its
	// `.data` stays STALE (it still holds the just-deleted conversation, keepPreviousData never clears it).
	// Injecting that stale payload would render a ghost row, so only feed the merge the selected conversation when
	// the selection is live (non-empty) AND the cached payload actually matches it.
	const selectedConversationForMerge =
		selectedConversationId.length > 0 && selectedConversationData?.id === selectedConversationId
			? selectedConversationData
			: undefined;
	const displayConversations = useMemo(
		() => mergeSelectedConversation(conversations, selectedConversationForMerge),
		[conversations, selectedConversationForMerge],
	);
	const activeConversation = displayConversations.find((conversation) => conversation.id === selectedConversationId);
	// Remote conversations are view-only on this node (server enforces the guard; this is the cosmetic UI hide).
	const isRemoteConversation = activeConversation?.origin === "remote";

	// Node-local feedback travels on each message in the loaded conversation:
	// derive the by-message map from the conversation read instead of firing a GET per assistant turn (which
	// 404'd and triggered a react-query retry storm before any feedback existed).
	const feedbackByMessageId = useMemo<Record<string, ChatMessageFeedback>>(() => {
		const byMessageId: Record<string, ChatMessageFeedback> = {};
		for (const message of activeConversation?.messages ?? []) {
			if (message.feedbackRating) {
				byMessageId[message.id] = {
					messageId: message.id,
					conversationId: message.conversationId,
					rating: message.feedbackRating,
					comment: message.feedbackComment,
					createdAt: message.createdAt,
					updatedAt: message.updatedAt ?? message.createdAt,
				};
			}
		}
		return byMessageId;
	}, [activeConversation?.messages]);
	const usedContextTokens = useMemo(
		() => deriveUsedContextTokens(activeConversation?.messages ?? []),
		[activeConversation?.messages],
	);

	return {
		conversations,
		conversationsIsLoading,
		conversationsIsError,
		conversationsError,
		maxMessageSizeKb,
		showArchivedConversations,
		setShowArchivedConversations,
		selectedConversationId,
		selectedConversationData,
		selectedConversationIsLoading,
		selectedConversationIsPlaceholderData,
		selectedConversationError,
		selectedConversationLoadFailed,
		isLoadingSelectedConversation,
		handleRetryLoadMessages,
		displayConversations,
		activeConversation,
		isRemoteConversation,
		feedbackByMessageId,
		usedContextTokens,
	};
}
