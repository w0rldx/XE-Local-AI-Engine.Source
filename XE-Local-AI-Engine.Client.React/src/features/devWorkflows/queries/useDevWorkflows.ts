// Server state for the Development Workflows surface. Every call goes through the generated hey-api `*Options()` /
// `*Mutation()` wrapped in withResponseValidation, exactly as `useWorkSessions.ts` does — no hand-wired axios, no
// hand-written request types (O11 / G8). The one exception is the paged event feed, which calls the generated SDK fn
// through `callWithResponseValidation` because hey-api generates no `*InfiniteOptions` for its cursor; see below.
//
// The generated query keys are single-element arrays `[{ _id: "<operationId>", path, query, … }]`, and TanStack
// matches them by PARTIAL DEEP equality. So `[{ _id, path: { runId } }]` invalidates every cached variant of one
// endpoint for one run while leaving the other runs' caches alone.

import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { listDevWorkflowRunEvents, type ListDevWorkflowRunEventsResponse } from "@/core/api/generated";
import {
	cancelDevWorkflowRunMutation,
	createDevWorkflowWorkItemMutation,
	decideDevWorkflowNodeRunMutation,
	deleteDevWorkflowWorkItemMutation,
	getDevWorkflowArtifactContentOptions,
	getDevWorkflowDefinitionOptions,
	getDevWorkflowNodeRunOptions,
	getDevWorkflowRunOptions,
	getDevWorkflowWorkItemOptions,
	listDevelopmentProjectsOptions,
	listDevWorkflowArtifactsOptions,
	listDevWorkflowDefinitionsOptions,
	listDevWorkflowRunEventsQueryKey,
	listDevWorkflowWorkItemsOptions,
	pauseDevWorkflowRunMutation,
	resumeDevWorkflowRunMutation,
	startDevWorkflowRunMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { callWithResponseValidation, withResponseValidation } from "@/core/api/ResponseValidation";
import { readDevWorkflowConflict } from "@/features/devWorkflows/api/DevWorkflowConflict";
import { isActiveDevWorkflowRunStatus, toDevWorkflowRunStatus } from "@/features/devWorkflows/models/DevWorkflowModels";

/** Generated operationIds, which are also the generated SDK fn names and the `_id` of every generated query key. */
export const devWorkflowQueryIds = {
	workItems: "listDevWorkflowWorkItems",
	workItem: "getDevWorkflowWorkItem",
	run: "getDevWorkflowRun",
	node: "getDevWorkflowNodeRun",
	events: "listDevWorkflowRunEvents",
	artifacts: "listDevWorkflowArtifacts",
	artifactContent: "getDevWorkflowArtifactContent",
	definitions: "listDevWorkflowDefinitions",
	definition: "getDevWorkflowDefinition",
} as const;

export type DevWorkflowQueryId = (typeof devWorkflowQueryIds)[keyof typeof devWorkflowQueryIds];

/**
 * Partial generated-query-key filter. Without a `path` it matches every cached variant of that endpoint (what the
 * list needs); with one, only the addressed run / work item / node run.
 */
export function devWorkflowInvalidationKey(
	operationId: string,
	path?: Readonly<Record<string, string>>,
): readonly [{ _id: string; path?: Readonly<Record<string, string>> }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return path ? [{ _id: operationId, path }] : [{ _id: operationId }];
}

/** The events feed is cursor-paged on `sinceSeq`; the artifact feed is `sinceSeq`-bounded and read whole. */
const FULL_FEED = { sinceSeq: 0 } as const;
export const devWorkflowEventsPageSize = 200;
/** Work-item list cadence while any listed run is still live (X16 Q7). A run-scoped hub cannot feed a list. */
const devWorkflowListPollIntervalMs = 5_000;

interface FeedOptions {
	/** Polling cadence while the hub is unavailable. `undefined` (the live case) means no polling at all. */
	readonly pollIntervalMs?: number;
	readonly enabled?: boolean;
}

function feedQuerySettings(id: string | undefined, options: FeedOptions) {
	return {
		enabled: (options.enabled ?? true) && Boolean(id),
		refetchInterval: options.pollIntervalMs ?? false,
	} as const;
}

/**
 * The work-item list. A run-scoped hub cannot feed a list, so this polls at 5s (X16 Q7) — but only while a listed run
 * is actually live. `latestRunStatus` is null for a work item that has never run, which is exactly a row that cannot
 * change on its own; polling it would be a timer burning for nothing.
 */
export function useDevWorkflowWorkItems() {
	return useQuery({
		...withResponseValidation(listDevWorkflowWorkItemsOptions()),
		refetchInterval: (query) => {
			const anyRunLive = (query.state.data?.items ?? []).some(
				(item) => Boolean(item.latestRunStatus) && isActiveDevWorkflowRunStatus(toDevWorkflowRunStatus(item.latestRunStatus)),
			);
			return anyRunLive ? devWorkflowListPollIntervalMs : false;
		},
	});
}

export function useDevWorkflowWorkItem(workItemId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(getDevWorkflowWorkItemOptions({ path: { workItemId: workItemId ?? "" } })),
		...feedQuerySettings(workItemId, options),
	});
}

/** The picker's definition list. Archived templates are hidden (Y14) — they are not startable. */
export function useDevWorkflowDefinitions(options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(listDevWorkflowDefinitionsOptions({ query: { includeArchived: false } })),
		enabled: options.enabled ?? true,
	});
}

/**
 * One definition WITH its graph, which the list summaries do not carry. Read only when a definition is actually being
 * previewed — a template's shape does not change while an operator looks at it, so this never polls.
 */
export function useDevWorkflowDefinition(definitionId: string | undefined) {
	return useQuery({
		...withResponseValidation(getDevWorkflowDefinitionOptions({ path: { definitionId: definitionId ?? "" } })),
		enabled: Boolean(definitionId),
	});
}

/** Dev Mode projects, for the create dialog's OPTIONAL project binding (X17). Read through the generated client. */
export function useDevelopmentProjectOptions(options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(listDevelopmentProjectsOptions()),
		enabled: options.enabled ?? true,
	});
}

/** R6 — the run payload: status, node-runs and the pinned graph in one call. Backs the whole centre pane. */
export function useDevWorkflowRun(runId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(getDevWorkflowRunOptions({ path: { runId: runId ?? "" } })),
		...feedQuerySettings(runId, options),
	});
}

export function useDevWorkflowNodeRun(runId: string | undefined, nodeRunId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(getDevWorkflowNodeRunOptions({ path: { runId: runId ?? "", nodeRunId: nodeRunId ?? "" } })),
		...feedQuerySettings(runId && nodeRunId ? nodeRunId : undefined, options),
	});
}

/**
 * The event log — the one feed that grows without bound, so the one that is cursor-paged. `sinceSeq` is an EXCLUSIVE
 * lower bound and the rows come back ascending, so "load more" reads the NEXT page from the watermark rather than
 * re-asking for a bigger first page. Growing `limit` instead stops at the server's 500-row clamp, which made every
 * event past the 500th unreachable for the rest of the run's life.
 *
 * Wired onto the generated SDK fn rather than `listDevWorkflowRunEventsOptions`, because hey-api emits `*InfiniteOptions`
 * only for the pagination parameters it recognises and `sinceSeq` is not one — the same reason `useBenchmarkRuns` pages
 * this way. The generated query KEY still identifies the cache entry, so `devWorkflowInvalidationKey(events, { runId })`
 * keeps matching it by partial deep equality and the hub's pings land where they always did.
 *
 * A ping invalidates this query and TanStack then refetches every loaded page in order, recomputing each cursor from
 * the page before it. That default is kept rather than resetting to page one: events are append-only and never
 * re-stamped, so the cursors are stable, and at A0 scale the feed is a page or two — while a reset would silently take
 * the operator back to the top of a log they had scrolled through.
 */
export function useDevWorkflowRunEvents(runId: string | undefined, options: FeedOptions = {}) {
	return useInfiniteQuery({
		// The first page's request, which is what this cache entry IS: the feed from the start, in pages of 200.
		queryKey: listDevWorkflowRunEventsQueryKey({
			path: { runId: runId ?? "" },
			query: { sinceSeq: 0, limit: devWorkflowEventsPageSize },
		}),
		initialPageParam: 0,
		// `hasMore` is the server's one-over-the-limit probe and `lastSequence` the highest sequence in the page, which is
		// the next exclusive cursor. Requiring it to ADVANCE is what stops a "load more" loop if a page ever reports more
		// without carrying a higher sequence.
		getNextPageParam: (
			lastPage: ListDevWorkflowRunEventsResponse,
			_pages: readonly ListDevWorkflowRunEventsResponse[],
			lastPageParam: number,
		) => (lastPage.hasMore === true && (lastPage.lastSequence ?? 0) > lastPageParam ? lastPage.lastSequence : undefined),
		queryFn: async ({ pageParam, signal }) => {
			const { data } = await callWithResponseValidation(
				listDevWorkflowRunEvents({
					path: { runId: runId ?? "" },
					query: { sinceSeq: pageParam, limit: devWorkflowEventsPageSize },
					signal,
					throwOnError: true,
				}),
			);
			return data;
		},
		// One ascending list, deduplicated on the sequence: an overlap at a page boundary — a refetch racing a
		// `fetchNextPage`, or a bound read one row too wide — must not render a row twice under a repeated React key.
		// Sequences are strictly increasing but NOT contiguous (the counter is shared with node-runs and artifacts), so
		// this sorts on the number rather than assuming 1..N.
		select: (data) =>
			[
				...new Map(data.pages.flatMap((page) => page.items ?? []).map((event) => [event.sequence ?? 0, event])).values(),
			].toSorted((left, right) => (left.sequence ?? 0) - (right.sequence ?? 0)),
		...feedQuerySettings(runId, options),
	});
}

export function useDevWorkflowArtifacts(runId: string | undefined, options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(listDevWorkflowArtifactsOptions({ path: { runId: runId ?? "" }, query: FULL_FEED })),
		...feedQuerySettings(runId, options),
	});
}

export function useDevWorkflowArtifactContent(runId: string | undefined, artifactId: string | undefined) {
	return useQuery({
		...withResponseValidation(
			getDevWorkflowArtifactContentOptions({ path: { runId: runId ?? "", artifactId: artifactId ?? "" } }),
		),
		enabled: Boolean(runId) && Boolean(artifactId),
	});
}

export function useCreateDevWorkflowWorkItem() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(createDevWorkflowWorkItemMutation()),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItems) }),
	});
}

export function useDeleteDevWorkflowWorkItem() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(deleteDevWorkflowWorkItemMutation()),
		// The delete removes the runs, their owned work sessions and their artifact bytes, so the caller must navigate
		// away rather than keep a route open on it. Only the list is refreshed here.
		onSuccess: () => queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItems) }),
	});
}

/**
 * Starting a run answers 202: the dispatcher picks the work up out of band, so the work item is re-read rather than
 * primed from the response. The work item is read off the call's own variables because the create dialog starts a run
 * on an item that did not exist when the hook was constructed.
 */
export function useStartDevWorkflowRun() {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(startDevWorkflowRunMutation()),
		onSuccess: async (_data, variables) => {
			const workItemId = variables.path?.workItemId;
			if (workItemId) {
				await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItem, { workItemId }) });
			}
			await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItems) });
		},
	});
}

/**
 * Pause / resume / cancel. All three are fire-and-forget 202s — the run reports `Pausing` / `Cancelling` until the
 * drain settles, which is why the toolbar must not flip straight to the terminal label.
 */
export function useDevWorkflowRunLifecycle(runId: string | undefined, workItemId: string | undefined) {
	const queryClient = useQueryClient();
	const refresh = async (): Promise<void> => {
		if (runId) {
			await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.run, { runId }) });
		}
		if (workItemId) {
			await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItem, { workItemId }) });
		}
		await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItems) });
	};

	const pause = useMutation({ ...withResponseValidation(pauseDevWorkflowRunMutation()), onSuccess: refresh });
	const resume = useMutation({ ...withResponseValidation(resumeDevWorkflowRunMutation()), onSuccess: refresh });
	const cancel = useMutation({ ...withResponseValidation(cancelDevWorkflowRunMutation()), onSuccess: refresh });

	return { pause, resume, cancel };
}

/**
 * The ONE decision surface (X3/Y7): a gate answer and a stuck node's Retry/Skip/Abandon travel the same route with the
 * same client-minted `operationId`. Invalidation is deliberate rather than optimistic — what follows a decision is the
 * dispatcher's work on its own clock, so the panel re-reads rather than predicting the next state.
 */
export function useDecideDevWorkflowNodeRun(runId: string | undefined, workItemId: string | undefined) {
	const queryClient = useQueryClient();

	const refresh = async (nodeRunId: string | undefined): Promise<void> => {
		if (runId) {
			await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.run, { runId }) });
			await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.events, { runId }) });
			if (nodeRunId) {
				await queryClient.invalidateQueries({
					queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.node, { runId, nodeRunId }),
				});
			}
		}
		if (workItemId) {
			await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItem, { workItemId }) });
		}
	};

	return useMutation({
		...withResponseValidation(decideDevWorkflowNodeRunMutation()),
		onSuccess: (_data, variables) => refresh(variables.path?.nodeRunId),
		// A 409 means the server disagrees with what this panel is showing — the gate was already answered, or the node
		// has moved on. Re-reading is what takes the live buttons away; without it the panel keeps offering a decision
		// on a settled gate, and every further click earns another 409.
		onError: (error, variables) => (readDevWorkflowConflict(error) ? refresh(variables.path?.nodeRunId) : undefined),
	});
}
