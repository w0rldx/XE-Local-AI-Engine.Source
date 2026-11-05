// The event feed for the Development Workflows surface: the one feed that grows without bound, so the one paged by a
// `sinceSeq` cursor. It uses the generated `*InfiniteOptions` with its own cursor rules; see `useDevWorkflowRunEvents`.

import { useInfiniteQuery } from "@tanstack/react-query";

import type { ListDevWorkflowRunEventsResponse } from "@/core/api/generated";
import { listDevWorkflowRunEventsInfiniteOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { type FeedOptions, feedQuerySettings } from "@/features/devWorkflows/queries/useDevWorkflows";

export const devWorkflowEventsPageSize = 200;
/**
 * The tail page asks for two windows' worth so it always reaches the run's true last sequence — see
 * `devWorkflowEventsAnchorParam`. Comfortably under the endpoint's 500-row server clamp.
 */
const devWorkflowEventsTailPageSize = devWorkflowEventsPageSize * 2;

/** Which end of the log the feed opens on. The operator switches ends; nothing else does. */
export type DevWorkflowEventsAnchor = "newest" | "oldest";

/**
 * The cursor the newest-first feed opens on, QUANTIZED to a page boundary.
 *
 * `sinceSeq` is an exclusive lower bound and the server answers forward, so "the newest page" is a cursor one page
 * below the run's high-water mark. Deriving it from `lastSequence` directly would mint a new cursor — and therefore a
 * new cache entry — on every single event, which throws away any older pages the operator has loaded. Quantizing to a
 * page boundary changes it once per page instead, and the tail request asks for TWO pages so it still spans from the
 * boundary all the way to `lastSequence` however far past the boundary that has moved.
 *
 * simplified: crossing a boundary on a live run does re-anchor and discard loaded history. That is once per 200
 * sequences, and the alternative — a cursor that moves with every event — discards it on every one. Exported so the
 * events tab can SAY so when it happens, rather than the older pages an operator had loaded silently disappearing.
 */
export function devWorkflowEventsAnchorParam(lastSequence: number | undefined, anchor: DevWorkflowEventsAnchor): number {
	if (anchor === "oldest" || !lastSequence || lastSequence <= devWorkflowEventsTailPageSize) {
		return 0;
	}
	// Windows are half-open `(start, start + pageSize]`, so sequence N sits in the window starting at `(N-1) / page`.
	const window = Math.floor((lastSequence - 1) / devWorkflowEventsPageSize) * devWorkflowEventsPageSize;
	return Math.max(0, window - devWorkflowEventsPageSize);
}

/**
 * The event log — the one feed that grows without bound, so the one that is cursor-paged. `sinceSeq` is an EXCLUSIVE
 * lower bound and the rows come back ascending. Growing `limit` instead stops at the server's 500-row clamp, which
 * made every event past the 500th unreachable for the rest of the run's life.
 *
 * **The feed opens on the NEWEST events, not the oldest.** A fan-out run — a materialization's whole point — writes
 * hundreds of events, and a feed that opened at sequence 1 pinned the operator to the run's first minute and made
 * "what is happening now" a dozen clicks away. There is no wire change behind this: the events endpoint takes `sinceSeq` and nothing
 * else, so the tail is reached by COMPUTING the cursor from `lastSequence`, which the run payload already carries, and
 * then walking BACKWARD a page at a time.
 *
 * Walking backward is exact rather than approximate because run sequences are strictly increasing and unique — the
 * counter is shared with node-runs and artifacts, so a window of N sequence NUMBERS holds at most N events. A request
 * for one window's width therefore returns that whole window and can skip nothing.
 *
 * `anchor: "oldest"` is the other end: the cursor is 0 and paging runs FORWARD off the server's `hasMore` probe, which
 * is the original behavior and is what an operator reading a run from its beginning wants.
 *
 * Built on the generated `listDevWorkflowRunEventsInfiniteOptions`. The page param is the whole `{ query }` object, not
 * a bare cursor, because the tail page also differs in `limit`; it overrides the key's query. The key still carries
 * `_id` and `path`, so `devWorkflowInvalidationKey(events, { runId })` matches it and the hub's pings land.
 *
 * A ping invalidates this query and TanStack then refetches every loaded page in order, recomputing each cursor from
 * the page before it. That default is kept rather than resetting to page one: events are append-only and never
 * re-stamped, so the cursors are stable, and a reset would silently take the operator back to the top of a log they
 * had scrolled through.
 */
export function useDevWorkflowRunEvents(
	runId: string | undefined,
	lastSequence: number | undefined,
	options: FeedOptions & { readonly anchor?: DevWorkflowEventsAnchor } = {},
) {
	const anchor = options.anchor ?? "newest";
	const anchorParam = devWorkflowEventsAnchorParam(lastSequence, anchor);
	// Only a genuine tail page spans two windows, which is what carries it past the quantized boundary to the run's
	// actual last sequence. Every backward page is exactly one window wide, and an anchor of 0 has no boundary to span.
	const path = { runId: runId ?? "" };
	const page = (sinceSeq: number) => ({
		path,
		query: {
			sinceSeq,
			limit: anchorParam > 0 && sinceSeq === anchorParam ? devWorkflowEventsTailPageSize : devWorkflowEventsPageSize,
		},
	});
	// The first page's request, which is what this cache entry IS: the feed from `anchorParam`, in pages of 200.
	const { queryKey, queryFn } = listDevWorkflowRunEventsInfiniteOptions({
		path,
		query: { sinceSeq: anchorParam, limit: devWorkflowEventsPageSize },
	});
	return useInfiniteQuery({
		queryKey,
		queryFn,
		initialPageParam: page(anchorParam),
		// Backward: the next page is the window below this one, and a cursor of 0 is the start of the log — there is
		// nothing older to ask for. Forward (the "oldest" anchor): `hasMore` is the server's one-over-the-limit probe and
		// `lastSequence` the highest sequence in the page, which is the next exclusive cursor. Requiring it to ADVANCE is
		// what stops a "load more" loop if a page ever reports more without carrying a higher sequence.
		getNextPageParam: (
			lastPage: ListDevWorkflowRunEventsResponse,
			_pages: readonly ListDevWorkflowRunEventsResponse[],
			lastPageParam: number | { readonly query?: { readonly sinceSeq?: number } },
		) => {
			const cursor = typeof lastPageParam === "number" ? lastPageParam : (lastPageParam.query?.sinceSeq ?? 0);
			// A cursor of 0 is the start of the log: there is nothing older, so the only honest continuation is the
			// server's own forward probe. That covers the short run whose whole feed IS the first page, and the run
			// payload not having landed yet — with no `lastSequence` there is no tail to anchor on, and answering
			// "nothing more" to a feed that has hundreds of unread events would be the truncation this replaced.
			if (anchorParam === 0) {
				return lastPage.hasMore === true && (lastPage.lastSequence ?? 0) > cursor ? page(lastPage.lastSequence ?? 0) : undefined;
			}
			return cursor <= 0 ? undefined : page(Math.max(0, cursor - devWorkflowEventsPageSize));
		},
		// One ascending list, deduplicated on the sequence: an overlap at a page boundary — a refetch racing a
		// `fetchNextPage`, or a bound read one row too wide — must not render a row twice under a repeated React key.
		// Sequences are strictly increasing but NOT contiguous (the counter is shared with node-runs and artifacts), so
		// this sorts on the number rather than assuming 1..N.
		select: (data) =>
			[...new Map(data.pages.flatMap((page) => page.items ?? []).map((event) => [event.sequence ?? 0, event])).values()].toSorted(
				(left, right) => (left.sequence ?? 0) - (right.sequence ?? 0),
			),
		...feedQuerySettings(runId, options),
	});
}
