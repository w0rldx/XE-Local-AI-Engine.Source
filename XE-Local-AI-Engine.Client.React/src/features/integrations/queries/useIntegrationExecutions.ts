import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import type { XeLocalAiEngineClientEndpointsIntegrationsV1IntegrationExecutionEventDto } from "@/core/api/generated";
import {
	cancelIntegrationExecutionMutation,
	getIntegrationExecutionEventsOptions,
	getIntegrationExecutionEventsQueryKey,
	getIntegrationExecutionOptions,
	listIntegrationExecutionsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import {
	toIntegrationExecution,
	toIntegrationExecutionDetail,
	toIntegrationExecutionEvent,
} from "@/features/integrations/models/IntegrationMappers";
import {
	type IntegrationExecutionFilters,
	integrationEventLimit,
	integrationPageSize,
} from "@/features/integrations/models/IntegrationModels";
import { integrationInvalidationKey, integrationQueryIds } from "@/features/integrations/queries/useIntegrationTriggers";

// Server state for integration executions. Every filter is a QUERY PARAMETER the backend accepts, so it rides in the
// generated query key too: changing a filter refetches against the whole table rather than narrowing the window the
// last read happened to return.

interface IntegrationQueryOptions {
	readonly enabled?: boolean;
	readonly refetchInterval?: number | false;
}

/** One page of a list read. Both bounds ride in the generated query key, so each page is its own cache entry. */
interface IntegrationListOptions extends IntegrationQueryOptions {
	readonly limit?: number;
	readonly offset?: number;
}

/**
 * One page of executions plus the count of rows the SAME filters match, which is what makes a page navigator
 * honest — without it a bounded window could only be described, never numbered.
 */
export function useIntegrationExecutions(filters: IntegrationExecutionFilters = {}, options: IntegrationListOptions = {}) {
	return useQuery({
		...withResponseValidation(
			listIntegrationExecutionsOptions({
				query: {
					...(filters.triggerId === undefined ? {} : { triggerId: filters.triggerId }),
					...(filters.sessionId === undefined ? {} : { sessionId: filters.sessionId }),
					// A repeated parameter: one chip can stand for the three active states.
					...(filters.status === undefined ? {} : { status: [...filters.status] }),
					limit: options.limit ?? integrationPageSize,
					offset: options.offset ?? 0,
				},
			}),
		),
		enabled: options.enabled ?? true,
		refetchInterval: options.refetchInterval,
		// `limit`/`offset` are part of the query key, so page 2 is a cache entry with no data of its own. Without the
		// previous page held over, `totalCount` would read as 0 for a render, the pager would compute one page, and its
		// clamp would send the operator back to page 1 while the page-2 request was still in flight.
		placeholderData: keepPreviousData,
		select: (data) => ({ items: data.items.map(toIntegrationExecution), totalCount: data.totalCount }),
	});
}

/** The per-execution audit record (principal, request id, key prefix) the list projection does not carry. */
export function useIntegrationExecution(executionId: string | null, options: IntegrationQueryOptions = {}) {
	return useQuery({
		...withResponseValidation(getIntegrationExecutionOptions({ path: { executionId: executionId ?? "" } })),
		enabled: executionId !== null && (options.enabled ?? true),
		refetchInterval: options.refetchInterval,
		select: toIntegrationExecutionDetail,
	});
}

/**
 * The persisted timeline, read by WATERMARK until the server runs out of rows. `sinceSeq` is an EXCLUSIVE lower
 * bound and the rows come back ascending, so a short page means "caught up" and a full one means "call again" —
 * the contract `ListIntegrationExecutionEventsRequest` states. Asking once for the validator's maximum instead
 * silently dropped every event past the 500th, and events ascend, so the row lost first is the terminal one that
 * says how the run ended.
 *
 * A loop inside `queryFn` rather than `useInfiniteQuery` (which is what the dev-workflows event feed uses): there
 * is no pager here. The timeline renders the whole log and re-reads it every 5 s, so one cache entry holding one
 * ascending array keeps the `select`/`refetchInterval` contract its two consumers already have.
 *
 * Each refetch re-pages from sequence 0. The log is append-only and short in the ordinary case, and a re-read from
 * the last watermark would need a merge against the previous cache entry to keep the rows it already had.
 */
export function useIntegrationExecutionEvents(executionId: string | null, options: IntegrationQueryOptions = {}) {
	return useQuery({
		// The generated key for the first page's request, so this cache entry stays identifiable by the same
		// partial-match filters every other integrations query is invalidated by.
		queryKey: getIntegrationExecutionEventsQueryKey({
			path: { executionId: executionId ?? "" },
			query: { sinceSeq: 0, limit: integrationEventLimit },
		}),
		queryFn: async (context) => {
			const items: XeLocalAiEngineClientEndpointsIntegrationsV1IntegrationExecutionEventDto[] = [];
			let sinceSeq = 0;
			for (;;) {
				// Each page is a full generated adapter — shared axios instance, response validation and the outer
				// query's AbortSignal, threaded by the runtime rather than by hand. The adapter's own `queryFn` reads
				// its request off `queryKey[0]`, so the page's key travels with it; the outer context supplies the
				// signal. `page.queryFn` is a function here because `queryOptions()` always emits one.
				const page = withResponseValidation(
					getIntegrationExecutionEventsOptions({
						path: { executionId: executionId ?? "" },
						query: { sinceSeq, limit: integrationEventLimit },
					}),
				);
				// biome-ignore lint/performance/noAwaitInLoops: watermark paging is sequential by definition — the next `sinceSeq` is read off the page before it, so there is nothing to run in parallel.
				const data = await page.queryFn!({ ...context, queryKey: page.queryKey });
				items.push(...data.items);
				// The next watermark is the highest sequence this page carried. Requiring it to ADVANCE is what stops a
				// full page that reports no higher sequence from looping forever.
				const nextSinceSeq = data.items.reduce((highest, event) => Math.max(highest, event.sequence), sinceSeq);
				if (data.items.length < integrationEventLimit || nextSinceSeq <= sinceSeq) {
					return items;
				}
				sinceSeq = nextSinceSeq;
			}
		},
		enabled: executionId !== null && (options.enabled ?? true),
		refetchInterval: options.refetchInterval,
		select: (data) => data.map(toIntegrationExecutionEvent),
	});
}

/**
 * Cancellation is REQUESTED, not applied: the endpoint answers 202 and a running turn stops when it observes the
 * token, so the row leaves its active status on a later poll rather than on this response. A 409 means the execution
 * already reached a terminal state, so it invalidates for the same reason a 202 does — the row on screen is the stale
 * one that provoked the request. `onSettled` rather than `onSuccess` because the refetch is right for BOTH outcomes,
 * and a refetch after any other failure only re-reads a list this page already polls every few seconds.
 */
export function useCancelIntegrationExecution() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(cancelIntegrationExecutionMutation()),
		onSettled: () =>
			queryClient.invalidateQueries({ queryKey: integrationInvalidationKey(integrationQueryIds.listExecutions) }),
	});
}
