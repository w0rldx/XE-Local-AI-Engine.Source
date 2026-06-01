// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { schedulerQueryKeys } from "@/features/scheduler/queries/SchedulerQueryKeys";

const { apiMock } = vi.hoisted(() => ({
	apiMock: {
		createScheduledJob: vi.fn(),
		updateScheduledJob: vi.fn(),
		deleteScheduledJob: vi.fn(),
		enableScheduledJob: vi.fn(),
		disableScheduledJob: vi.fn(),
		triggerScheduledJob: vi.fn(),
		cancelScheduledJobRun: vi.fn(),
	},
}));

vi.mock("@/features/scheduler/api/SchedulerApi", () => ({
	createScheduledJob: apiMock.createScheduledJob,
	updateScheduledJob: apiMock.updateScheduledJob,
	deleteScheduledJob: apiMock.deleteScheduledJob,
	enableScheduledJob: apiMock.enableScheduledJob,
	disableScheduledJob: apiMock.disableScheduledJob,
	triggerScheduledJob: apiMock.triggerScheduledJob,
	cancelScheduledJobRun: apiMock.cancelScheduledJobRun,
	// Read functions are imported by the module but unused in these mutation tests.
	getScheduledJob: vi.fn(),
	getScheduledJobRun: vi.fn(),
	listScheduledJobRuns: vi.fn(),
	listScheduledJobs: vi.fn(),
	listScheduledJobTemplates: vi.fn(),
}));

import {
	useCancelScheduledJobRun,
	useCreateScheduledJob,
	useDeleteScheduledJob,
	useSetScheduledJobEnabled,
	useTriggerScheduledJob,
	useUpdateScheduledJob,
} from "@/features/scheduler/queries/useScheduler";

const sampleRequest = {
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

// Captures the queryKey of every invalidateQueries call so a test can assert which caches a mutation touched.
const invalidatedKeys: unknown[] = [];

function makeWrapper() {
	invalidatedKeys.length = 0;
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	vi.spyOn(queryClient, "invalidateQueries").mockImplementation((filters) => {
		invalidatedKeys.push((filters as { queryKey?: unknown } | undefined)?.queryKey);
		return Promise.resolve();
	});
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	return { Wrapper };
}

describe("useScheduler mutations", () => {
	beforeEach(() => {
		apiMock.createScheduledJob.mockResolvedValue({ id: "job-1" });
		apiMock.updateScheduledJob.mockResolvedValue({ id: "job-1" });
		apiMock.deleteScheduledJob.mockResolvedValue(undefined);
		apiMock.enableScheduledJob.mockResolvedValue(undefined);
		apiMock.disableScheduledJob.mockResolvedValue(undefined);
		apiMock.triggerScheduledJob.mockResolvedValue(undefined);
		apiMock.cancelScheduledJobRun.mockResolvedValue({ outcome: "Requested", cancellationRequestedAtUtc: 123 });
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("create invalidates the jobs list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useCreateScheduledJob(), { wrapper: Wrapper });

		result.current.mutate(sampleRequest);

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(invalidatedKeys).toContainEqual(schedulerQueryKeys.jobsRoot());
	});

	it("update invalidates the jobs list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useUpdateScheduledJob(), { wrapper: Wrapper });

		result.current.mutate({ id: "job-1", request: sampleRequest });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(invalidatedKeys).toContainEqual(schedulerQueryKeys.jobsRoot());
	});

	it("delete invalidates the jobs list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useDeleteScheduledJob(), { wrapper: Wrapper });

		result.current.mutate("job-1");

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(invalidatedKeys).toContainEqual(schedulerQueryKeys.jobsRoot());
	});

	it("enable calls the enable endpoint and invalidates the jobs list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useSetScheduledJobEnabled(), { wrapper: Wrapper });

		result.current.mutate({ id: "job-1", enabled: true });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(apiMock.enableScheduledJob).toHaveBeenCalledWith("job-1");
		expect(apiMock.disableScheduledJob).not.toHaveBeenCalled();
		expect(invalidatedKeys).toContainEqual(schedulerQueryKeys.jobsRoot());
	});

	it("disable calls the disable endpoint", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useSetScheduledJobEnabled(), { wrapper: Wrapper });

		result.current.mutate({ id: "job-1", enabled: false });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(apiMock.disableScheduledJob).toHaveBeenCalledWith("job-1");
		expect(apiMock.enableScheduledJob).not.toHaveBeenCalled();
	});

	it("trigger invalidates the run history", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useTriggerScheduledJob(), { wrapper: Wrapper });

		result.current.mutate("job-1");

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(invalidatedKeys).toContainEqual(schedulerQueryKeys.runsRoot());
		expect(invalidatedKeys).toContainEqual(schedulerQueryKeys.runRoot());
	});

	it("cancel invalidates the run history and per-run detail", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useCancelScheduledJobRun(), { wrapper: Wrapper });

		result.current.mutate("run-1");

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(invalidatedKeys).toContainEqual(schedulerQueryKeys.runsRoot());
		expect(invalidatedKeys).toContainEqual(schedulerQueryKeys.runRoot());
	});

	it("cancel still refreshes the run history when the run is already terminal (409 rejects)", async () => {
		// The run finished between render and click → backend 409 → axios rejects. onSettled must still invalidate so
		// the stale "active" row is replaced with the terminal state.
		apiMock.cancelScheduledJobRun.mockRejectedValue(new Error("Request failed with status code 409"));
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useCancelScheduledJobRun(), { wrapper: Wrapper });

		result.current.mutate("run-1");

		await waitFor(() => expect(result.current.isError).toBe(true));

		expect(invalidatedKeys).toContainEqual(schedulerQueryKeys.runsRoot());
		expect(invalidatedKeys).toContainEqual(schedulerQueryKeys.runRoot());
	});
});
