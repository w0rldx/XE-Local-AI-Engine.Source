import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import { nodeChatQueryKeys } from "@/core/chat/queries/NodeChatQueryKeys";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { isNodeChatReadOnlyConflict } from "@/features/chat/api/NodeChatConflict";
import { chatErrorMessage, chatWriteErrorMessage } from "@/features/chat/models/ChatErrorMessage";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";
import { forgetWebContentAutoAccept } from "@/features/chat/stores/WebContentAutoAcceptStore";

interface ChatConversationActionsInput {
	cacheConversation: (conversation: ChatConversationModel) => void;
	refreshConversation: (conversationId: string) => Promise<void>;
	setStreamError: (message: string | undefined) => void;
	requestedConversationId: string;
	setRequestedConversationId: (conversationId: string) => void;
	markConversationTitled: (conversationId: string) => void;
	forgetConversationTitle: (conversationId: string) => void;
	markConversationDeleted: (conversationId: string) => void;
	unmarkConversationDeleted: (conversationId: string) => void;
}

/** The conversation list's writes: rename, pin, archive, temporary, plain and delete. */
export function useChatConversationActions({
	cacheConversation,
	refreshConversation,
	setStreamError,
	requestedConversationId,
	setRequestedConversationId,
	markConversationTitled,
	forgetConversationTitle,
	markConversationDeleted,
	unmarkConversationDeleted,
}: ChatConversationActionsInput) {
	const { t } = useTranslation();
	const { confirm } = useConfirm();
	const queryClient = useQueryClient();
	const [mutatingConversationId, setMutatingConversationId] = useState<string | undefined>();

	const runConversationMutation = useCallback(
		async (conversationId: string, mutate: () => Promise<ChatConversationModel>): Promise<void> => {
			setStreamError(undefined);
			setMutatingConversationId(conversationId);
			try {
				const updated = await mutate();
				cacheConversation(updated);
				await refreshConversation(conversationId);
			} catch (error) {
				setStreamError(chatWriteErrorMessage(error, t));
				// Remote-origin conversations are view-only; the node rejects writes with a 409. Refresh so the UI
				// re-reflects authoritative (unchanged) server state.
				if (isNodeChatReadOnlyConflict(error)) {
					await refreshConversation(conversationId);
				}
			} finally {
				setMutatingConversationId(undefined);
			}
		},
		[cacheConversation, refreshConversation, setStreamError, t],
	);

	const handleRenameConversation = useCallback(
		(conversationId: string, title: string): void => {
			markConversationTitled(conversationId);
			runConversationMutation(conversationId, () => nodeChatAdapter.renameConversation(conversationId, title)).catch(
				(error: unknown) => setStreamError(chatErrorMessage(error)),
			);
		},
		[markConversationTitled, runConversationMutation, setStreamError],
	);

	const handleToggleConversationPinned = useCallback(
		(conversationId: string, isPinned: boolean): void => {
			runConversationMutation(conversationId, () => nodeChatAdapter.setConversationPinned(conversationId, isPinned)).catch(
				(error: unknown) => setStreamError(chatErrorMessage(error)),
			);
		},
		[runConversationMutation, setStreamError],
	);

	const handleToggleConversationArchived = useCallback(
		(conversationId: string, archived: boolean): void => {
			runConversationMutation(conversationId, () => nodeChatAdapter.setConversationArchived(conversationId, archived)).catch(
				(error: unknown) => setStreamError(chatErrorMessage(error)),
			);
		},
		[runConversationMutation, setStreamError],
	);

	// Toggle a conversation "temporary" (memory-excluded). A temporary conversation still USES existing memory; it
	// just won't teach the agent new memory from this thread. PATCHes the conversation via the same mutation path as
	// pin/archive so the cache + selected-conversation query refresh from authoritative server state.
	const handleToggleConversationMemoryExcluded = useCallback(
		(conversationId: string, memoryExcluded: boolean): void => {
			runConversationMutation(conversationId, () =>
				nodeChatAdapter.setConversationMemoryExcluded(conversationId, memoryExcluded),
			).catch((error: unknown) => setStreamError(chatErrorMessage(error)));
		},
		[runConversationMutation, setStreamError],
	);

	// Set a conversation's plain mode and custom system prompt; later turns only. Same mutation path as pin/archive.
	const handleSetConversationPlain = useCallback(
		(conversationId: string, isPlain: boolean, plainSystemPrompt: string | undefined): void => {
			runConversationMutation(conversationId, () =>
				nodeChatAdapter.setConversationPlain(conversationId, isPlain, plainSystemPrompt),
			)
				// The estimate's key does not carry the mode or the prompt, so a cached one is now wrong.
				.then(() => queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.contextEstimates(conversationId) }))
				.catch((error: unknown) => setStreamError(chatErrorMessage(error)));
		},
		[queryClient, runConversationMutation, setStreamError],
	);

	const deleteConversationMutation = useMutation({
		mutationFn: (conversationId: string) => nodeChatAdapter.deleteConversation(conversationId),
		onSuccess: async (_result, conversationId) => {
			// Drop the deleted thread from caches and, when it was the open one, clear the selection so the
			// list falls back to the newest remaining conversation.
			queryClient.removeQueries({ queryKey: nodeChatQueryKeys.conversation(conversationId) });
			if (requestedConversationId === conversationId) {
				setRequestedConversationId("");
			}
			forgetConversationTitle(conversationId);
			forgetWebContentAutoAccept(conversationId);
			await queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversations() });
		},
	});

	const deleteConversation = useCallback(
		async (conversationId: string, skipConfirm: boolean): Promise<void> => {
			// Shift-click skips the confirm and deletes immediately, mirroring the platform client.
			if (!skipConfirm) {
				const confirmed = await confirm({
					title: t("pages.chat.conversationList.delete", "Delete"),
					description: t("pages.chat.conversationList.deleteConfirm", "Delete this conversation? This cannot be undone."),
					confirmationText: t("pages.chat.conversationList.delete", "Delete"),
					cancellationText: t("common.cancel", "Cancel"),
				});
				if (!confirmed) {
					return;
				}
			}

			markConversationDeleted(conversationId);

			setStreamError(undefined);
			setMutatingConversationId(conversationId);
			try {
				await deleteConversationMutation.mutateAsync(conversationId);
			} catch (error) {
				// The delete failed, so the conversation still exists and stays visible/selectable. Roll back the
				// pre-emptive "deleted" marker set above — otherwise the streaming loop's has(id) guard
				// treats the surviving thread as removed and silently refuses to stream to it: a permanently
				// un-chattable zombie until reload.
				unmarkConversationDeleted(conversationId);
				setStreamError(chatWriteErrorMessage(error, t));
				if (isNodeChatReadOnlyConflict(error)) {
					await refreshConversation(conversationId);
				}
			} finally {
				setMutatingConversationId(undefined);
			}
		},
		[
			confirm,
			deleteConversationMutation,
			markConversationDeleted,
			refreshConversation,
			setStreamError,
			t,
			unmarkConversationDeleted,
		],
	);

	const handleDeleteConversation = useCallback(
		(conversationId: string, skipConfirm: boolean): void => {
			deleteConversation(conversationId, skipConfirm).catch((error: unknown) => setStreamError(chatErrorMessage(error)));
		},
		[deleteConversation, setStreamError],
	);

	return {
		mutatingConversationId,
		handleRenameConversation,
		handleToggleConversationPinned,
		handleToggleConversationArchived,
		handleToggleConversationMemoryExcluded,
		handleSetConversationPlain,
		handleDeleteConversation,
	};
}
