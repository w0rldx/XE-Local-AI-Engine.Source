// The run's event log: the one graph workflow feed that grows without bound, so the one that is cursor-paged.

import { useInfiniteQuery } from "@tanstack/react-query";

import { listGraphWorkflowRunEventsInfiniteOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import type {
	GraphWorkflowRunEventResponse,
	ListGraphWorkflowRunEventsResponse,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { type FeedOptions, feedQuerySettings } from "@/features/graphWorkflows/queries/useGraphWorkflows";

/** One ascending, deduplicated trail plus the first page's truncation flag. See {@link useGraphWorkflowRunEvents}. */
export interface GraphWorkflowEventFeed {
	readonly events: readonly GraphWorkflowRunEventResponse[];
	readonly replayTruncated: boolean;
}

/**
 * The event log — the one feed that grows without bound, so the one that is cursor-paged. `afterSeq` is an EXCLUSIVE
 * lower bound, the rows come back ascending, and the server caps a page at `EventReplayLimit` (200) reporting
 * `replayTruncated` when it read one row past that cap. There is no `limit` parameter to grow instead.
 *
 * Paging runs FORWARD from sequence 0: the trail is the run's audit history and a run's first event is the one that
 * explains the rest. `lastSeq` is the highest sequence the page actually carried — the server's own resume rule, and
 * exactly what the hub snapshot hands back — so the next cursor is that number. Requiring it to ADVANCE is what stops
 * a "load more" loop if a page ever reports truncation without carrying a higher sequence.
 *
 * Built on the generated `listGraphWorkflowRunEventsInfiniteOptions` (`afterSeq` is a configured pagination keyword):
 * a numeric page param becomes `query.afterSeq`. Its key still carries `_id` and `path`, so
 * `graphWorkflowInvalidationKey(events, { runId })` matches it by partial deep equality and the hub's pings land.
 *
 * A ping invalidates this query and TanStack then refetches every loaded page in order, recomputing each cursor from
 * the page before it. Events are append-only and never re-stamped, so the cursors are stable.
 */
export function useGraphWorkflowRunEvents(runId: string | undefined, options: FeedOptions = {}) {
	// The first page's request, which is what this cache entry IS: the whole feed, from the start of the log.
	const { queryKey, queryFn } = listGraphWorkflowRunEventsInfiniteOptions({
		path: { runId: runId ?? "" },
		query: { afterSeq: 0 },
	});
	return useInfiniteQuery({
		queryKey,
		queryFn,
		initialPageParam: 0,
		// Every page param this feed mints is a number; the generated type also admits a `{ query }` object.
		getNextPageParam: (lastPage: ListGraphWorkflowRunEventsResponse, _pages, lastPageParam) =>
			lastPage.replayTruncated === true && typeof lastPageParam === "number" && (lastPage.lastSeq ?? 0) > lastPageParam
				? lastPage.lastSeq
				: undefined,
		// One ascending list, deduplicated on the sequence: an overlap at a page boundary — a refetch racing a
		// `fetchNextPage` — must not render a row twice under a repeated React key. Sequences are strictly increasing
		// but NOT contiguous (the counter is shared with the node runs), so this sorts on the number itself.
		//
		// `replayTruncated` comes from the FIRST page: it is the answer to "is this list the whole run", which is what
		// the events tab says out loud rather than presenting a silently short trail as the full history.
		select: (data): GraphWorkflowEventFeed => ({
			events: [
				...new Map(data.pages.flatMap((page) => page.events ?? []).map((event) => [event.seq ?? 0, event])).values(),
			].toSorted((left, right) => (left.seq ?? 0) - (right.seq ?? 0)),
			replayTruncated: data.pages[0]?.replayTruncated === true,
		}),
		...feedQuerySettings(runId, options),
	});
}
