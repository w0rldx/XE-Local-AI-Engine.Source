import type { ScheduledJobRunFilters } from "@/features/scheduler/models/SchedulerModels";

// Stable, order-independent string key for the run-history filters so two equivalent filter objects map to the
// same query cache entry (the SignalR hub invalidates by the runs() prefix, so the exact filter shape only
// needs to be a deterministic suffix).
function runsFilterKey(filters: ScheduledJobRunFilters): string {
	return JSON.stringify({
		status: filters.status ?? null,
		fromUtc: filters.fromUtc ?? null,
		toUtc: filters.toUtc ?? null,
		scheduledJobId: filters.scheduledJobId ?? null,
	});
}

export const schedulerQueryKeys = {
	all: () => ["scheduler"] as const,
	templates: () => [...schedulerQueryKeys.all(), "templates"] as const,
	// jobsRoot is the prefix shared by every includeDeleted variant of the jobs list, so invalidating it refetches
	// both the active and the include-deleted views in one call.
	jobsRoot: () => [...schedulerQueryKeys.all(), "jobs"] as const,
	jobs: (includeDeleted: boolean) => [...schedulerQueryKeys.jobsRoot(), includeDeleted] as const,
	job: (id: string) => [...schedulerQueryKeys.all(), "job", id] as const,
	// runsRoot / runRoot are the prefixes the hub + mutations invalidate so every filtered run-history view and
	// per-run query refreshes regardless of the exact filter object in play.
	runsRoot: () => [...schedulerQueryKeys.all(), "runs"] as const,
	runs: (filters: ScheduledJobRunFilters) => [...schedulerQueryKeys.runsRoot(), runsFilterKey(filters)] as const,
	runRoot: () => [...schedulerQueryKeys.all(), "run"] as const,
	run: (runId: string) => [...schedulerQueryKeys.runRoot(), runId] as const,
};
