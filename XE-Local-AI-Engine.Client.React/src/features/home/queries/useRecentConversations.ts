import { useQuery } from "@tanstack/react-query";

import { listNodeChatConversationsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { RECENT_CONVERSATION_FETCH_LIMIT, toRecentConversations } from "@/features/home/models/HomeModels";

// The welcome card's "Continue" and the recent-chats card read the same entry, so Home asks once. No hub: Home is a
// glance. Chat writes its own cache keys, not this one, so the entry counts as stale at once and every visit to Home
// refetches it rather than showing the list from before the last chat.
export function useRecentConversations() {
	return useQuery({
		...listNodeChatConversationsOptions({ query: { includeArchived: false, limit: RECENT_CONVERSATION_FETCH_LIMIT } }),
		staleTime: 0,
		select: (data) => toRecentConversations(data.items),
	});
}
