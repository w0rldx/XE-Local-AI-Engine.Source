import { useCallback, useEffect, useMemo, useState } from "react";

import type { ChatConversationModel } from "@/features/chat/models/ChatModels";

interface RevisionSelectionState {
	conversationId: string;
	selectedPath: Record<string, string>;
}

/** Owns the persisted revision baseline and conversation-scoped in-session overrides for the selected thread. */
export function useChatRevisionSelection(selectedConversationId: string, loadedConversation: ChatConversationModel | undefined) {
	const [baseline, setBaseline] = useState<RevisionSelectionState>({ conversationId: "", selectedPath: {} });
	const [overrides, setOverrides] = useState<RevisionSelectionState>({ conversationId: "", selectedPath: {} });

	useEffect(() => {
		if (!loadedConversation || loadedConversation.id !== selectedConversationId) {
			return;
		}
		setBaseline((current) =>
			current.conversationId === selectedConversationId
				? current
				: { conversationId: selectedConversationId, selectedPath: loadedConversation.selectedPath ?? {} },
		);
	}, [loadedConversation, selectedConversationId]);

	const activeRevisionByGroup = useMemo<Record<string, string>>(() => {
		const loadedPath = loadedConversation?.id === selectedConversationId ? (loadedConversation.selectedPath ?? {}) : {};
		const persistedPath = baseline.conversationId === selectedConversationId ? baseline.selectedPath : loadedPath;
		const overridePath = overrides.conversationId === selectedConversationId ? overrides.selectedPath : {};
		return { ...persistedPath, ...overridePath };
	}, [baseline, loadedConversation, overrides, selectedConversationId]);

	const selectRevision = useCallback(
		(variantGroupId: string, messageId: string): Record<string, string> => {
			const nextSelection = { ...activeRevisionByGroup, [variantGroupId]: messageId };
			setOverrides((current) => ({
				conversationId: selectedConversationId,
				selectedPath: {
					...(current.conversationId === selectedConversationId ? current.selectedPath : {}),
					[variantGroupId]: messageId,
				},
			}));
			return nextSelection;
		},
		[activeRevisionByGroup, selectedConversationId],
	);

	return { activeRevisionByGroup, selectRevision };
}
