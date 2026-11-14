import { type QueryClient, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import type { CancelScheduledJobRunResponse, CreateScheduledJobResponse, UpdateScheduledJobResponse } from "@/core/api/generated";
import {
	cancelScheduledJobRunMutation,
	createScheduledJobMutation,
	deleteNodeChatConversationMutation,
	deleteScheduledJobMutation,
	disableScheduledJobMutation,
	enableScheduledJobMutation,
	getNodeChatConversationOptions,
	getScheduledJobRunOptions,
	listScheduledJobRunsOptions,
	listScheduledJobsOptions,
	listScheduledJobTemplatesOptions,
	triggerScheduledJobMutation,
	updateScheduledJobMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { keepPreviousPageOf } from "@/core/ui/components/TablePagination/useTablePagination";
import { toScheduledJob, toScheduledJobRun, toScheduledJobTemplate } from "@/features/scheduler/models/SchedulerMappers";
import type { ScheduledJobRun, ScheduledJobRunFilters } from "@/features/scheduler/models/SchedulerModels";

// Server state for the scheduler management surface. Reads use the generated hey-api `*Options()` (which wire the
// shared axios instance + TanStack Query AbortSignal automatically) and a TanStack `select` that maps the
// optional-field generated response into the stricter domain view-model. Mutations invalidate by the generated query key's `_id` discriminator (partial-object match), so every cached
// variant of an endpoint refetches. The SignalR hub (useSchedulerHub) layers live invalidation on top of these for
// server-pushed changes — TanStack Query stays the authoritative source.

// Generated keys are object arrays; TanStack partial matching on `_id` invalidates every endpoint variant.
export const schedulerQueryIds = {
	listJobs: "listScheduledJobs",
	listRuns: "listScheduledJobRuns",
	getRun: "getScheduledJobRun",
	listUpcoming: "listUpcomingScheduledJobs",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one scheduler endpoint. */
export function schedulerInvalidationKey(operationId: string): readonly [{ _id: string }] {
	return [{ _id: operationId }];
}

export function useScheduledJobTemplates() {
	return useQuery({
		...listScheduledJobTemplatesOptions(),
		select: (data) => (data.items ?? []).map(toScheduledJobTemplate),
	});
}

export function useScheduledJobs(includeDeleted = false) {
	return useQuery({
		...listScheduledJobsOptions({ query: { includeDeleted } }),
		select: (data) => (data.items ?? []).map(toScheduledJob),
	});
}

interface ScheduledJobRunsQueryOptions {
	readonly enabled?: boolean;
	readonly refetchInterval?: number | false;
	readonly limit: number;
	readonly offset: number;
}

/** One server page of run history, newest first, and how many runs the filters match in all. */
export interface ScheduledJobRunPage {
	readonly items: ScheduledJobRun[];
	readonly totalCount: number;
}

// The server pages run history (a 30-day window of a frequent job is thousands of runs). An absent filter stays
// undefined, which neither the query key nor the query string carries.
export function useScheduledJobRuns(filters: ScheduledJobRunFilters, options: ScheduledJobRunsQueryOptions) {
	const query = { ...filters, limit: options.limit, offset: options.offset };
	return useQuery({
		...listScheduledJobRunsOptions({ query }),
		enabled: options.enabled ?? true,
		refetchInterval: options.refetchInterval,
		// The next page has no data yet: the previous page of the same filters keeps `totalCount`, so the pager does not
		// clamp back to page 1 mid-request. Another filter's runs are never shown in its place.
		placeholderData: keepPreviousPageOf(query),
		select: (data): ScheduledJobRunPage => ({ items: data.items.map(toScheduledJobRun), totalCount: data.totalCount }),
	});
}

// Imperative one-shot fetch of run history (mapped to the domain view-model) for callers that reconcile OUTSIDE the
// React Query hook lifecycle — e.g. a SignalR catch-up after the model-fit hub (re)connects. staleTime:0 forces a
// fresh read each call so the reconciliation never sees cached data. It reads the newest 200 runs, the server's
// ceiling: a catch-up is scoped to one job and a short window, so more than that would itself be a reason to reload.
export async function fetchScheduledJobRuns(
	queryClient: QueryClient,
	filters: ScheduledJobRunFilters = {},
): Promise<ScheduledJobRun[]> {
	const data = await queryClient.fetchQuery({
		...listScheduledJobRunsOptions({ query: { ...filters, limit: 200 } }),
		staleTime: 0,
	});
	return data.items.map(toScheduledJobRun);
}

// One run's detail. Disabled until a run id is supplied so the detail query only fires when a run is selected.
export function useScheduledJobRun(runId: string | null) {
	return useQuery({
		...getScheduledJobRunOptions({ path: { runId: runId ?? "" } }),
		enabled: runId !== null,
		select: toScheduledJobRun,
	});
}

// A successful agent run's saved prompt and answer, read only while the operator has it open from the run detail.
export function useScheduledRunAnswer(conversationId: string | null) {
	return useQuery({
		...getNodeChatConversationOptions({ path: { conversationId: conversationId ?? "" } }),
		enabled: conversationId !== null,
	});
}

// Deleting the answer purges it at once; the run detail refetches and drops its link.
export function useDeleteScheduledRunAnswer() {
	const queryClient = useQueryClient();

	return useMutation({
		...deleteNodeChatConversationMutation(),
		onSuccess: () => invalidateRuns(queryClient),
	});
}

// A job edit can move its next fire, so Home's upcoming list refreshes with the job list.
async function invalidateJobs(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	await Promise.all([
		queryClient.invalidateQueries({ queryKey: schedulerInvalidationKey(schedulerQueryIds.listJobs) }),
		queryClient.invalidateQueries({ queryKey: schedulerInvalidationKey(schedulerQueryIds.listUpcoming) }),
	]);
}

async function invalidateRuns(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	await Promise.all([
		queryClient.invalidateQueries({ queryKey: schedulerInvalidationKey(schedulerQueryIds.listRuns) }),
		queryClient.invalidateQueries({ queryKey: schedulerInvalidationKey(schedulerQueryIds.getRun) }),
	]);
}

export function useCreateScheduledJob() {
	const queryClient = useQueryClient();

	return useMutation({
		...createScheduledJobMutation(),
		onSuccess: (_data: CreateScheduledJobResponse) => invalidateJobs(queryClient),
	});
}

export function useUpdateScheduledJob() {
	const queryClient = useQueryClient();

	return useMutation({
		...updateScheduledJobMutation(),
		onSuccess: (_data: UpdateScheduledJobResponse) => invalidateJobs(queryClient),
	});
}

export interface SetScheduledJobEnabledVariables {
	id: string;
	enabled: boolean;
}

// Enable/disable share one hook so the list row can flip a single switch. The two generated mutations carry the
// same `{ path: { scheduledJobId } }` variable shape and neither returns a body, so this hook keeps the domain
// `{ id, enabled }` variable and dispatches to the matching generated mutationFn. Both verbs refresh the jobs list.
export function useSetScheduledJobEnabled() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async ({ id, enabled }: SetScheduledJobEnabledVariables): Promise<void> => {
			const options = enabled ? enableScheduledJobMutation() : disableScheduledJobMutation();
			await options.mutationFn?.({ path: { scheduledJobId: id } }, undefined as never);
		},
		onSuccess: () => invalidateJobs(queryClient),
	});
}

export function useDeleteScheduledJob() {
	const queryClient = useQueryClient();

	return useMutation({
		...deleteScheduledJobMutation(),
		onSuccess: () => invalidateJobs(queryClient),
	});
}

// Manual trigger enqueues a run, so it invalidates the run history (the definition list is unchanged).
export function useTriggerScheduledJob() {
	const queryClient = useQueryClient();

	return useMutation({
		...triggerScheduledJobMutation(),
		onSuccess: () => invalidateRuns(queryClient),
	});
}

// Cancel requests termination of an active run, so it invalidates the run history + per-run detail. Uses onSettled
// (not onSuccess) because the backend returns 409 when the run finished between render and click — axios rejects on
// 409, so onSuccess would skip the refetch and leave a stale "active" row; onSettled refreshes either way.
export function useCancelScheduledJobRun() {
	const queryClient = useQueryClient();

	return useMutation({
		...cancelScheduledJobRunMutation(),
		onSettled: (_data: CancelScheduledJobRunResponse | undefined) => invalidateRuns(queryClient),
	});
}
