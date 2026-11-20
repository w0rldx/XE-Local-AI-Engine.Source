import { useNavigate } from "@tanstack/react-router";
import { useCallback } from "react";

import { nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { usePendingChatConversationStore } from "@/core/ui/stores/PendingChatConversationStore";

// Opens `/chat` on a given conversation or on a new one. Both travel through the core hand-off store: `/chat` has no
// search schema, and the chat page consumes the store on mount.
export function useChatHandOff() {
	const navigate = useNavigate();
	const { setPendingConversationId, requestNewConversation } = usePendingChatConversationStore((state) => state.actions);

	const openConversation = useCallback(
		(conversationId: string) => {
			setPendingConversationId(conversationId);
			navigate({ to: nodeRoutePaths.chat }).catch(() => undefined);
		},
		[navigate, setPendingConversationId],
	);
	const startNewConversation = useCallback(() => {
		requestNewConversation();
		navigate({ to: nodeRoutePaths.chat }).catch(() => undefined);
	}, [navigate, requestNewConversation]);

	return { openConversation, startNewConversation };
}
