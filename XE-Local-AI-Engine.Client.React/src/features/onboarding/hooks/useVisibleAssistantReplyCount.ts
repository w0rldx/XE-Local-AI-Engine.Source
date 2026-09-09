import type { QueryClient } from "@tanstack/react-query";
import { useCallback, useSyncExternalStore } from "react";

import { countVisibleAssistantReplies } from "@/features/onboarding/data/TourAdvanceSignals";

/**
 * Number of assistant replies currently visible in the query cache, live. Only subscribes while `active`, so a tour
 * step that does not watch for a reply costs nothing.
 */
export function useVisibleAssistantReplyCount(queryClient: QueryClient, active: boolean): number {
	const subscribe = useCallback(
		(onStoreChange: () => void) => {
			if (!active) {
				return () => undefined;
			}
			return queryClient.getQueryCache().subscribe(onStoreChange);
		},
		[active, queryClient],
	);
	const getSnapshot = useCallback(() => (active ? countVisibleAssistantReplies(queryClient) : 0), [active, queryClient]);

	return useSyncExternalStore(subscribe, getSnapshot);
}
