import { useQueryClient } from "@tanstack/react-query";
import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeChatQueryKeys } from "@/core/chat/queries/NodeChatQueryKeys";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { chatErrorMessage, chatWriteErrorMessage } from "@/features/chat/models/ChatErrorMessage";
import type { ChatFeedbackRating } from "@/features/chat/models/ChatModels";

interface ChatMessageActionsInput {
	selectedConversationId: string;
	activeRevisionByGroup: Record<string, string>;
	selectRevision: (variantGroupId: string, messageId: string) => Record<string, string>;
	setRequestedConversationId: (conversationId: string) => void;
	setStreamError: (message: string | undefined) => void;
}

/** The writes a single message offers: revision navigation, branching and feedback. */
export function useChatMessageActions({
	selectedConversationId,
	activeRevisionByGroup,
	selectRevision,
	setRequestedConversationId,
	setStreamError,
}: ChatMessageActionsInput) {
	const { t } = useTranslation();
	const queryClient = useQueryClient();
	const [pendingFeedbackMessageId, setPendingFeedbackMessageId] = useState<string | undefined>();

	const handleSelectRevision = useCallback(
		(variantGroupId: string, messageId: string): void => {
			const nextSelection = selectRevision(variantGroupId, messageId);
			// Persist the navigated selection so a reload restores it even without sending a message. Fire-and-forget,
			// consistent with other adapter calls; a failure surfaces in the error banner but never blocks the UI.
			if (selectedConversationId) {
				nodeChatAdapter
					.persistSelectedPath(selectedConversationId, nextSelection)
					.catch((error: unknown) => setStreamError(chatErrorMessage(error)));
			}
		},
		[selectRevision, selectedConversationId, setStreamError],
	);

	const branchConversation = useCallback(
		async (messageId: string): Promise<void> => {
			setStreamError(undefined);
			try {
				// Send the visible active-revision selection so the branched thread copies the revisions the user was
				// viewing upstream, not always the newest sibling. Empty ⇒ server keeps its newest-per-group default.
				const selectedRevisions = Object.keys(activeRevisionByGroup).length > 0 ? activeRevisionByGroup : undefined;
				const result = await nodeChatAdapter.branchConversation(selectedConversationId, messageId, selectedRevisions);
				// Surface the branched Origin=Local conversation: refresh history and open it.
				await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversations() });
				setRequestedConversationId(result.branchedConversationId ?? "");
			} catch (error) {
				setStreamError(chatWriteErrorMessage(error, t));
			}
		},
		[activeRevisionByGroup, queryClient, selectedConversationId, setRequestedConversationId, setStreamError, t],
	);

	const handleBranch = useCallback(
		(messageId: string): void => {
			if (!selectedConversationId) {
				return;
			}

			branchConversation(messageId).catch((error: unknown) => setStreamError(chatErrorMessage(error)));
		},
		[branchConversation, selectedConversationId, setStreamError],
	);

	const submitFeedback = useCallback(
		async (messageId: string, rating: ChatFeedbackRating, comment: string | undefined): Promise<void> => {
			setStreamError(undefined);
			setPendingFeedbackMessageId(messageId);
			try {
				await nodeChatAdapter.setMessageFeedback(selectedConversationId, messageId, rating, comment);
				// Feedback is read from the conversation's messages (not a per-message GET); refetch the
				// conversation so the just-saved rating/comment re-renders.
				await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversation(selectedConversationId) });
			} catch (error) {
				setStreamError(chatWriteErrorMessage(error, t));
			} finally {
				setPendingFeedbackMessageId(undefined);
			}
		},
		[queryClient, selectedConversationId, setStreamError, t],
	);

	const handleSubmitFeedback = useCallback(
		(messageId: string, rating: ChatFeedbackRating, comment: string | undefined): void => {
			if (!selectedConversationId) {
				return;
			}

			submitFeedback(messageId, rating, comment).catch((error: unknown) => setStreamError(chatErrorMessage(error)));
		},
		[selectedConversationId, setStreamError, submitFeedback],
	);

	return { handleSelectRevision, handleBranch, handleSubmitFeedback, pendingFeedbackMessageId };
}
