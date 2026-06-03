// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ScheduledJobRun } from "@/features/scheduler/models/SchedulerModels";

const { modelFitMock, schedulerMock, toastMock } = vi.hoisted(() => ({
	modelFitMock: {
		useRefreshRecommendations: vi.fn(),
	},
	schedulerMock: {
		useScheduledJobRuns: vi.fn(),
	},
	toastMock: {
		error: vi.fn(),
		success: vi.fn(),
		warn: vi.fn(),
		warning: vi.fn(),
	},
}));

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (key: string) => key }),
}));

vi.mock("@/features/model-fit/queries/useModelFit", () => modelFitMock);
vi.mock("@/features/scheduler/queries/useScheduler", () => schedulerMock);
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

import { useModelFitRefreshFeedback } from "@/features/model-fit/hooks/useModelFitRefreshFeedback";

function run(overrides: Partial<ScheduledJobRun> = {}): ScheduledJobRun {
	return {
		id: "run-1",
		scheduledJobId: "job-mf",
		templateId: "model-recommendation-check",
		triggeredBy: "Schedule",
		status: "Succeeded",
		scheduledFireTimeUtc: null,
		actualFireTimeUtc: Date.now(),
		completedAtUtc: Date.now() + 100,
		durationMs: 100,
		summary: "Completed.",
		errorMessage: null,
		cancellationRequestedAtUtc: null,
		createdAtUtc: Date.now(),
		...overrides,
	};
}

function makeMutation() {
	return {
		mutate: vi.fn((_scheduledJobId: string, _options?: { onError?: () => void }) => undefined),
		isPending: false,
		error: null,
	};
}

function queryWrapper({ children }: { children: ReactNode }) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

describe("useModelFitRefreshFeedback", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		modelFitMock.useRefreshRecommendations.mockReturnValue(makeMutation());
		schedulerMock.useScheduledJobRuns.mockReturnValue({ data: [], isLoading: false, error: null });
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("shows the success toast from REST run reconciliation when the hub event was missed", async () => {
		const refreshMutation = makeMutation();
		modelFitMock.useRefreshRecommendations.mockReturnValue(refreshMutation);
		schedulerMock.useScheduledJobRuns
			.mockReturnValueOnce({ data: [], isLoading: false, error: null })
			.mockReturnValueOnce({ data: [run()], isLoading: false, error: null });

		const { result, rerender } = renderHook(() => useModelFitRefreshFeedback(), { wrapper: queryWrapper });

		act(() => {
			result.current.refresh("job-mf");
		});
		rerender();

		await waitFor(() => {
			expect(toastMock.success).toHaveBeenCalledWith(
				"pages.modelFit.recommendations.toasts.success",
				expect.objectContaining({ id: "model-fit-refresh-run-1", autoClose: 5000 }),
			);
		});
		expect(refreshMutation.mutate).toHaveBeenCalledWith("job-mf", expect.objectContaining({ onError: expect.any(Function) }));
		expect(schedulerMock.useScheduledJobRuns).toHaveBeenCalledWith(
			expect.objectContaining({ scheduledJobId: "job-mf" }),
			expect.objectContaining({ enabled: true, refetchInterval: 1000 }),
		);
	});
});
