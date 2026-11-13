import { useDebouncedValue } from "@mantine/hooks";
import { useQuery } from "@tanstack/react-query";

import { nodeChatQueryKeys } from "@/core/chat/queries/NodeChatQueryKeys";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import type { ChatConversationSearchModel } from "@/features/chat/models/ChatModels";

const SEARCH_DEBOUNCE_MS = 250;
const INDEXING_REFETCH_MS = 5_000;

/**
 * The node's content search for the sidebar query, debounced. Undefined while the query is blank, while the request for
 * the current query is in flight, or when it failed: the sidebar then filters the conversations it has loaded.
 */
export function useConversationContentSearch(query: string, includeArchived: boolean): ChatConversationSearchModel | undefined {
	const [debouncedQuery] = useDebouncedValue(query.trim(), SEARCH_DEBOUNCE_MS);
	const { data } = useQuery({
		queryKey: nodeChatQueryKeys.conversationSearch(debouncedQuery, includeArchived),
		queryFn: ({ signal }) => nodeChatAdapter.searchConversations(debouncedQuery, includeArchived, { signal }),
		enabled: debouncedQuery.length > 0,
		// Bounded fallback for an active job without a hub: while the node is still indexing, the answer may be partial,
		// so re-ask until it reports the backfill done; then stop.
		refetchInterval: (search) => (search.state.data?.indexing ? INDEXING_REFETCH_MS : false),
	});

	// The debounce lags the box: results for an older query must not stand in for the text now typed.
	return debouncedQuery.length > 0 && debouncedQuery === query.trim() ? data : undefined;
}
