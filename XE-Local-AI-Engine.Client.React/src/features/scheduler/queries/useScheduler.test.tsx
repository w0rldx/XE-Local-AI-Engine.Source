// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Mock the generated hey-api TanStack mutation factories. Each returns an object carrying a `mutationFn` the hook
// spreads (after withResponseValidation) into useMutation; the hooks layer their own onSuccess/onSettled
// invalidation on top. The factory mocks let a test assert the variable shape the hook forwarded to the wire.
const { mutationFns } = vi.hoisted(() => ({
	mutationFns: {
		createScheduledJob: vi.fn(),
		updateScheduledJob: vi.fn(),
		deleteScheduledJob: vi.fn(),
		enableScheduledJob: vi.fn(),
		disableScheduledJob: vi.fn(),
		triggerScheduledJob: vi.fn(),
		cancelScheduledJobRun: vi.fn(),
	},
}));

// Builds the single-element generated query key shape the read-side factory mocks return. Centralizes the `_id`
// discriminator literal (which trips biome's naming-convention rule) in one suppressed spot.
function fakeQueryKey(operationId: string): unknown {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	createScheduledJobMutation: () => ({ mutationFn: mutationFns.createScheduledJob }),
	updateScheduledJobMutation: () => ({ mutationFn: mutationFns.updateScheduledJob }),
	deleteScheduledJobMutation: () => ({ mutationFn: mutationFns.deleteScheduledJob }),
	enableScheduledJobMutation: () => ({ mutationFn: mutationFns.enableScheduledJob }),
	disableScheduledJobMutation: () => ({ mutationFn: mutationFns.disableScheduledJob }),
	triggerScheduledJobMutation: () => ({ mutationFn: mutationFns.triggerScheduledJob }),
	cancelScheduledJobRunMutation: () => ({ mutationFn: mutationFns.cancelScheduledJobRun }),
	// Read-side factories are imported by the module under test but unused in these mutation tests.
	getScheduledJobOptions: vi.fn(() => ({ queryKey: fakeQueryKey("getScheduledJob"), queryFn: vi.fn() })),
	getScheduledJobRunOptions: vi.fn(() => ({ queryKey: fakeQueryKey("getScheduledJobRun"), queryFn: vi.fn() })),
	listScheduledJobRunsOptions: vi.fn(() => ({ queryKey: fakeQueryKey("listScheduledJobRuns"), queryFn: vi.fn() })),
	listScheduledJobsOptions: vi.fn(() => ({ queryKey: fakeQueryKey("listScheduledJobs"), queryFn: vi.fn() })),
	listScheduledJobTemplatesOptions: vi.fn(() => ({
		queryKey: fakeQueryKey("listScheduledJobTemplates"),
		queryFn: vi.fn(),
	})),
}));

import {
	schedulerInvalidationKey,
	schedulerQueryIds,
	useCancelScheduledJobRun,
	useCreateScheduledJob,
	useDeleteScheduledJob,
	useSetScheduledJobEnabled,
	useTriggerScheduledJob,
	useUpdateScheduledJob,
} from "@/features/scheduler/queries/useScheduler";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

const sampleBody = {
	templateId: "cleanup",
	displayName: "Nightly cleanup",
	description: null,
	scheduleKind: "Cron" as const,
	cronExpression: "0 0 3 * * ?",
	intervalSeconds: null,
	repeatCount: null,
	startAtUtc: null,
	endAtUtc: null,
	timeZoneId: "UTC",
	misfirePolicy: "Smart" as const,
	preventOverlap: true,
	maxRuntimeSeconds: null,
	parameters: null,
};

// Generated query keys are single-element arrays `[{ _id: "<operationId>", ... }]`. The hooks invalidate by the
// `_id` partial object, so these are the keys a test expects each mutation to have invalidated (built via the same
// production helper the hooks use).
const jobsKey = schedulerInvalidationKey(schedulerQueryIds.listJobs);
const runsKey = schedulerInvalidationKey(schedulerQueryIds.listRuns);
const runKey = schedulerInvalidationKey(schedulerQueryIds.getRun);

// Captures the queryKey of every invalidateQueries call so a test can assert which caches a mutation touched.
const invalidatedKeys: unknown[] = [];

function makeWrapper() {
	invalidatedKeys.length = 0;
	const { wrapper, queryClient } = createProvidersWrapper();
	vi.spyOn(queryClient, "invalidateQueries").mockImplementation((filters) => {
		invalidatedKeys.push((filters as { queryKey?: unknown } | undefined)?.queryKey);
		return Promise.resolve();
	});
	return { wrapper };
}

describe("useScheduler mutations", () => {
	beforeEach(() => {
		mutationFns.createScheduledJob.mockResolvedValue({ id: "job-1" });
		mutationFns.updateScheduledJob.mockResolvedValue({ id: "job-1" });
		mutationFns.deleteScheduledJob.mockResolvedValue(undefined);
		mutationFns.enableScheduledJob.mockResolvedValue(undefined);
		mutationFns.disableScheduledJob.mockResolvedValue(undefined);
		mutationFns.triggerScheduledJob.mockResolvedValue(undefined);
		mutationFns.cancelScheduledJobRun.mockResolvedValue({ outcome: "Requested", cancellationRequestedAtUtc: 123 });
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("create forwards the body and invalidates the jobs list", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useCreateScheduledJob(), { wrapper });

		result.current.mutate({ body: sampleBody });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		// TanStack v5 passes (variables, context) — assert the first arg.
		expect(mutationFns.createScheduledJob.mock.calls[0]?.[0]).toEqual({ body: sampleBody });
		expect(invalidatedKeys).toContainEqual(jobsKey);
	});

	it("update forwards path + body and invalidates the jobs list", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useUpdateScheduledJob(), { wrapper });

		result.current.mutate({ path: { scheduledJobId: "job-1" }, body: sampleBody });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.updateScheduledJob.mock.calls[0]?.[0]).toEqual({
			path: { scheduledJobId: "job-1" },
			body: sampleBody,
		});
		expect(invalidatedKeys).toContainEqual(jobsKey);
	});

	it("delete forwards the path and invalidates the jobs list", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useDeleteScheduledJob(), { wrapper });

		result.current.mutate({ path: { scheduledJobId: "job-1" } });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.deleteScheduledJob.mock.calls[0]?.[0]).toEqual({ path: { scheduledJobId: "job-1" } });
		expect(invalidatedKeys).toContainEqual(jobsKey);
	});

	it("enable calls the enable endpoint and invalidates the jobs list", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useSetScheduledJobEnabled(), { wrapper });

		result.current.mutate({ id: "job-1", enabled: true });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.enableScheduledJob.mock.calls[0]?.[0]).toEqual({ path: { scheduledJobId: "job-1" } });
		expect(mutationFns.disableScheduledJob).not.toHaveBeenCalled();
		expect(invalidatedKeys).toContainEqual(jobsKey);
	});

	it("disable calls the disable endpoint", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useSetScheduledJobEnabled(), { wrapper });

		result.current.mutate({ id: "job-1", enabled: false });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.disableScheduledJob.mock.calls[0]?.[0]).toEqual({ path: { scheduledJobId: "job-1" } });
		expect(mutationFns.enableScheduledJob).not.toHaveBeenCalled();
	});

	it("trigger forwards the path and invalidates the run history", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useTriggerScheduledJob(), { wrapper });

		result.current.mutate({ path: { scheduledJobId: "job-1" } });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.triggerScheduledJob.mock.calls[0]?.[0]).toEqual({ path: { scheduledJobId: "job-1" } });
		expect(invalidatedKeys).toContainEqual(runsKey);
		expect(invalidatedKeys).toContainEqual(runKey);
	});

	it("cancel forwards the run path and invalidates the run history and per-run detail", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useCancelScheduledJobRun(), { wrapper });

		result.current.mutate({ path: { runId: "run-1" } });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationFns.cancelScheduledJobRun.mock.calls[0]?.[0]).toEqual({ path: { runId: "run-1" } });
		expect(invalidatedKeys).toContainEqual(runsKey);
		expect(invalidatedKeys).toContainEqual(runKey);
	});

	it("cancel still refreshes the run history when the run is already terminal (409 rejects)", async () => {
		// The run finished between render and click → backend 409 → axios rejects. onSettled must still invalidate so
		// the stale "active" row is replaced with the terminal state.
		mutationFns.cancelScheduledJobRun.mockRejectedValue(new Error("Request failed with status code 409"));
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useCancelScheduledJobRun(), { wrapper });

		result.current.mutate({ path: { runId: "run-1" } });

		await waitFor(() => expect(result.current.isError).toBe(true));

		expect(invalidatedKeys).toContainEqual(runsKey);
		expect(invalidatedKeys).toContainEqual(runKey);
	});
});
