import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	cancelScheduledJobRun,
	createScheduledJob,
	deleteScheduledJob,
	disableScheduledJob,
	enableScheduledJob,
	getScheduledJob,
	getScheduledJobRun,
	listScheduledJobRuns,
	listScheduledJobs,
	listScheduledJobTemplates,
	type SaveScheduledJobRequestDto,
	triggerScheduledJob,
	updateScheduledJob,
} from "@/features/scheduler/api/SchedulerApi";
import type { ScheduledJobRunFilters } from "@/features/scheduler/models/SchedulerModels";
import { schedulerQueryKeys } from "@/features/scheduler/queries/SchedulerQueryKeys";

// Server state for the scheduler management surface. All reads wire the TanStack Query AbortSignal into the
// axios request (per repo React standards). Mutations invalidate the relevant caches: definition mutations
// refresh the jobs list; trigger/cancel touch the run history. The SignalR hub (useSchedulerHub) layers live
// invalidation on top of these for server-pushed changes — TanStack Query stays the authoritative source.

export function useScheduledJobTemplates() {
	return useQuery({
		queryKey: schedulerQueryKeys.templates(),
		queryFn: ({ signal }) => listScheduledJobTemplates({ signal }),
	});
}

export function useScheduledJobs(includeDeleted = false) {
	return useQuery({
		queryKey: schedulerQueryKeys.jobs(includeDeleted),
		queryFn: ({ signal }) => listScheduledJobs(includeDeleted, { signal }),
	});
}

// One job definition. Disabled until an id is supplied so the detail query only fires when a job is selected.
export function useScheduledJob(id: string | null) {
	return useQuery({
		queryKey: schedulerQueryKeys.job(id ?? ""),
		queryFn: ({ signal }) => getScheduledJob(id ?? "", { signal }),
		enabled: id !== null,
	});
}

export function useScheduledJobRuns(filters: ScheduledJobRunFilters = {}) {
	return useQuery({
		queryKey: schedulerQueryKeys.runs(filters),
		queryFn: ({ signal }) => listScheduledJobRuns(filters, { signal }),
	});
}

// One run's detail. Disabled until a run id is supplied so the detail query only fires when a run is selected.
export function useScheduledJobRun(runId: string | null) {
	return useQuery({
		queryKey: schedulerQueryKeys.run(runId ?? ""),
		queryFn: ({ signal }) => getScheduledJobRun(runId ?? "", { signal }),
		enabled: runId !== null,
	});
}

function invalidateJobs(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: schedulerQueryKeys.jobsRoot() });
}

function invalidateRuns(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return Promise.all([
		queryClient.invalidateQueries({ queryKey: schedulerQueryKeys.runsRoot() }),
		queryClient.invalidateQueries({ queryKey: schedulerQueryKeys.runRoot() }),
	]).then(() => undefined);
}

export function useCreateScheduledJob() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (request: SaveScheduledJobRequestDto) => createScheduledJob(request),
		onSuccess: () => invalidateJobs(queryClient),
	});
}

export interface UpdateScheduledJobVariables {
	id: string;
	request: SaveScheduledJobRequestDto;
}

export function useUpdateScheduledJob() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: ({ id, request }: UpdateScheduledJobVariables) => updateScheduledJob(id, request),
		onSuccess: () => invalidateJobs(queryClient),
	});
}

export interface SetScheduledJobEnabledVariables {
	id: string;
	enabled: boolean;
}

// Enable/disable share one hook so the list row can flip a single switch. Both verbs refresh the jobs list;
// neither returns a body, so the cache is refetched rather than patched.
export function useSetScheduledJobEnabled() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: ({ id, enabled }: SetScheduledJobEnabledVariables) =>
			enabled ? enableScheduledJob(id) : disableScheduledJob(id),
		onSuccess: () => invalidateJobs(queryClient),
	});
}

export function useDeleteScheduledJob() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (id: string) => deleteScheduledJob(id),
		onSuccess: () => invalidateJobs(queryClient),
	});
}

// Manual trigger enqueues a run, so it invalidates the run history (the definition list is unchanged).
export function useTriggerScheduledJob() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (id: string) => triggerScheduledJob(id),
		onSuccess: () => invalidateRuns(queryClient),
	});
}

// Cancel requests termination of an active run, so it invalidates the run history + per-run detail. Uses onSettled
// (not onSuccess) because the backend returns 409 when the run finished between render and click — axios rejects on
// 409, so onSuccess would skip the refetch and leave a stale "active" row; onSettled refreshes either way.
export function useCancelScheduledJobRun() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (runId: string) => cancelScheduledJobRun(runId),
		onSettled: () => invalidateRuns(queryClient),
	});
}
