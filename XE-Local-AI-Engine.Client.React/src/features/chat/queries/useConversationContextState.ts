import { useQuery } from "@tanstack/react-query";

import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";

/**
 * The conversation's distilled context state and synopsis. Fetched only while `enabled` (the drawer is open), and
 * always refetched when it opens again (staleTime 0) so a compaction run since the last look is shown. Resolves to
 * null for an unknown conversation (404).
 */
export function useConversationContextState(conversationId: string, { enabled }: { enabled: boolean }) {
	return useQuery({
		queryKey: nodeChatQueryKeys.conversationContextState(conversationId),
		queryFn: ({ signal }) => nodeChatAdapter.getConversationContextState(conversationId, { signal }),
		enabled: enabled && conversationId.length > 0,
		staleTime: 0,
	});
}
