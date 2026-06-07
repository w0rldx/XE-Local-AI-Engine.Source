import { type QueryClient, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import type { CancelScheduledJobRunResponse, CreateScheduledJobResponse, UpdateScheduledJobResponse } from "@/core/api/generated";
import {
	cancelScheduledJobRunMutation,
	createScheduledJobMutation,
	deleteScheduledJobMutation,
	disableScheduledJobMutation,
	enableScheduledJobMutation,
	getScheduledJobRunOptions,
	listScheduledJobRunsOptions,
	listScheduledJobsOptions,
	listScheduledJobTemplatesOptions,
	triggerScheduledJobMutation,
	updateScheduledJobMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toScheduledJob, toScheduledJobRun, toScheduledJobTemplate } from "@/features/scheduler/models/SchedulerMappers";
import type { ScheduledJobRun, ScheduledJobRunFilters } from "@/features/scheduler/models/SchedulerModels";

// Server state for the scheduler management surface. Reads use the generated hey-api `*Options()` (which wire the
// shared axios instance + TanStack Query AbortSignal automatically) and a TanStack `select` that maps the
// optional-field generated response into the stricter domain view-model. Every generated options object is wrapped
// in withResponseValidation so a zod response-shape failure surfaces as an ApiError (never a raw ZodError).
// Mutations invalidate by the generated query key's `_id` discriminator (partial-object match), so every cached
// variant of an endpoint refetches. The SignalR hub (useSchedulerHub) layers live invalidation on top of these for
// server-pushed changes — TanStack Query stays the authoritative source.

// The generated query keys are single-element arrays `[{ _id: "<operationId>", ... }]`. Invalidating with just the
// `_id` partial object matches every cached variant of that endpoint (TanStack partial-object matching). The
// operationIds equal the generated SDK fn names. Centralized here (and reused by useSchedulerHub) so the literal
// `_id` key — which trips biome's naming-convention rule — is constructed in exactly one place.
export const schedulerQueryIds = {
	listJobs: "listScheduledJobs",
	listRuns: "listScheduledJobRuns",
	getRun: "getScheduledJobRun",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one scheduler endpoint. */
export function schedulerInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

export function useScheduledJobTemplates() {
	return useQuery({
		...withResponseValidation(listScheduledJobTemplatesOptions()),
		select: (data) => (data.items ?? []).map(toScheduledJobTemplate),
	});
}

export function useScheduledJobs(includeDeleted = false) {
	return useQuery({
		...withResponseValidation(listScheduledJobsOptions({ query: { includeDeleted } })),
		select: (data) => (data.items ?? []).map(toScheduledJob),
	});
}

// One job definition. Disabled until an id is supplied so the detail query only fires when a job is selected.
interface ScheduledJobRunsQueryOptions {
	readonly enabled?: boolean;
	readonly refetchInterval?: number | false;
}

// Shared run-history query options (validated). Only the active filters ride the query string; an absent filter is
// left undefined. Reused by the hook and by the imperative one-shot fetch so both build the key/validation identically.
function scheduledJobRunsQueryOptions(filters: ScheduledJobRunFilters) {
	return withResponseValidation(
		listScheduledJobRunsOptions({
			query: {
				...(filters.status !== undefined ? { status: filters.status } : {}),
				...(filters.fromUtc !== undefined ? { fromUtc: filters.fromUtc } : {}),
				...(filters.toUtc !== undefined ? { toUtc: filters.toUtc } : {}),
				...(filters.scheduledJobId !== undefined ? { scheduledJobId: filters.scheduledJobId } : {}),
			},
		}),
	);
}

export function useScheduledJobRuns(filters: ScheduledJobRunFilters = {}, options: ScheduledJobRunsQueryOptions = {}) {
	return useQuery({
		...scheduledJobRunsQueryOptions(filters),
		enabled: options.enabled ?? true,
		refetchInterval: options.refetchInterval,
		select: (data) => (data.items ?? []).map(toScheduledJobRun),
	});
}

// Imperative one-shot fetch of run history (mapped to the domain view-model) for callers that reconcile OUTSIDE the
// React Query hook lifecycle — e.g. a SignalR catch-up after the model-fit hub (re)connects. staleTime:0 forces a
// fresh read each call so the reconciliation never sees cached data.
export async function fetchScheduledJobRuns(
	queryClient: QueryClient,
	filters: ScheduledJobRunFilters = {},
): Promise<ScheduledJobRun[]> {
	const data = await queryClient.fetchQuery({ ...scheduledJobRunsQueryOptions(filters), staleTime: 0 });
	return (data.items ?? []).map(toScheduledJobRun);
}

// One run's detail. Disabled until a run id is supplied so the detail query only fires when a run is selected.
export function useScheduledJobRun(runId: string | null) {
	return useQuery({
		...withResponseValidation(getScheduledJobRunOptions({ path: { runId: runId ?? "" } })),
		enabled: runId !== null,
		select: toScheduledJobRun,
	});
}

function invalidateJobs(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: schedulerInvalidationKey(schedulerQueryIds.listJobs) });
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
		...withResponseValidation(createScheduledJobMutation()),
		onSuccess: (_data: CreateScheduledJobResponse) => invalidateJobs(queryClient),
	});
}

export function useUpdateScheduledJob() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(updateScheduledJobMutation()),
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
			const options = withResponseValidation(enabled ? enableScheduledJobMutation() : disableScheduledJobMutation());
			await options.mutationFn?.({ path: { scheduledJobId: id } }, undefined as never);
		},
		onSuccess: () => invalidateJobs(queryClient),
	});
}

export function useDeleteScheduledJob() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(deleteScheduledJobMutation()),
		onSuccess: () => invalidateJobs(queryClient),
	});
}

// Manual trigger enqueues a run, so it invalidates the run history (the definition list is unchanged).
export function useTriggerScheduledJob() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(triggerScheduledJobMutation()),
		onSuccess: () => invalidateRuns(queryClient),
	});
}

// Cancel requests termination of an active run, so it invalidates the run history + per-run detail. Uses onSettled
// (not onSuccess) because the backend returns 409 when the run finished between render and click — axios rejects on
// 409, so onSuccess would skip the refetch and leave a stale "active" row; onSettled refreshes either way.
export function useCancelScheduledJobRun() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(cancelScheduledJobRunMutation()),
		onSettled: (_data: CancelScheduledJobRunResponse | undefined) => invalidateRuns(queryClient),
	});
}
