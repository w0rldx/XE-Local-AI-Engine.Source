// Server state for the Development Workflows surface. Every call goes through the generated hey-api `*Options()` /
// `*Mutation()` wrapped in withResponseValidation, exactly as `useWorkSessions.ts` does — no hand-wired axios, no
// hand-written request types (O11 / G8).
//
// The generated query keys are single-element arrays `[{ _id: "<operationId>", path, query, … }]`, and TanStack
// matches them by PARTIAL DEEP equality. So `[{ _id, path: { runId } }]` invalidates every cached variant of one
// endpoint for one run while leaving the other runs' caches alone.

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	cancelDevWorkflowRunMutation,
	createDevWorkflowWorkItemMutation,
	decideDevWorkflowNodeRunMutation,
	deleteDevWorkflowWorkItemMutation,
	getDevWorkflowArtifactContentOptions,
	getDevWorkflowNodeRunOptions,
	getDevWorkflowRunOptions,
	getDevWorkflowWorkItemOptions,
	listDevelopmentProjectsOptions,
	listDevWorkflowArtifactsOptions,
	listDevWorkflowDefinitionsOptions,
	listDevWorkflowRunEventsOptions,
	listDevWorkflowWorkItemsOptions,
	pauseDevWorkflowRunMutation,
	resumeDevWorkflowRunMutation,
	startDevWorkflowRunMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

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

/** The events feed is server-paged (`limit`/`hasMore`); the artifact feed is `sinceSeq`-bounded and read whole. */
const FULL_FEED = { sinceSeq: 0 } as const;
export const devWorkflowEventsPageSize = 200;
/** The server clamps `limit` at 500 (`DevWorkflowRequestLimits.MaxEventPageSize`); past it "load more" would lie. */
export const devWorkflowEventsMaxLimit = 500;
/** Work-item list cadence while any listed run is still live (X16 Q7). A run-scoped hub cannot feed a list. */
export const devWorkflowListPollIntervalMs = 5_000;

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

export function useDevWorkflowWorkItems(options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(listDevWorkflowWorkItemsOptions()),
		refetchInterval: options.pollIntervalMs ?? false,
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

export function useDevWorkflowRunEvents(runId: string | undefined, limit: number, options: FeedOptions = {}) {
	return useQuery({
		...withResponseValidation(listDevWorkflowRunEventsOptions({ path: { runId: runId ?? "" }, query: { sinceSeq: 0, limit } })),
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

/** Starting a run answers 202: the dispatcher picks the work up out of band, so the work item is re-read, not primed. */
export function useStartDevWorkflowRun(workItemId: string) {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(startDevWorkflowRunMutation()),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItem, { workItemId }) });
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
	return useMutation({
		...withResponseValidation(decideDevWorkflowNodeRunMutation()),
		onSuccess: async (_data, variables) => {
			if (runId) {
				await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.run, { runId }) });
				await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.events, { runId }) });
			}
			const nodeRunId = variables.path?.nodeRunId;
			if (runId && nodeRunId) {
				await queryClient.invalidateQueries({
					queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.node, { runId, nodeRunId }),
				});
			}
			if (workItemId) {
				await queryClient.invalidateQueries({ queryKey: devWorkflowInvalidationKey(devWorkflowQueryIds.workItem, { workItemId }) });
			}
		},
	});
}
