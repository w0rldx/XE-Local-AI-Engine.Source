// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { useModelFitManagementStore } from "@/features/model-fit/stores/ModelFitManagementStore";
import type { ScheduledJob } from "@/features/scheduler/models/SchedulerModels";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (_key: string, defaultValue?: string, options?: Record<string, unknown>) => {
			let text = defaultValue ?? _key;
			if (options) {
				for (const [name, value] of Object.entries(options)) {
					text = text.replace(`{{${name}}}`, String(value));
				}
			}
			return text;
		},
	}),
}));

const navigateMock = vi.fn();
// Keep the real router exports (the page's useModelPull import chain transitively pulls in routeTree.gen, which calls
// createRootRouteWithContext at module-eval time); override only useNavigate so navigation is observable.
vi.mock("@tanstack/react-router", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@tanstack/react-router")>();
	return {
		...actual,
		useNavigate: () => navigateMock,
	};
});

const { hooksMock, schedulerMock, hubMock } = vi.hoisted(() => ({
	hooksMock: {
		useLatestRecommendations: vi.fn(),
		useApprovedImages: vi.fn(),
		useRefreshRecommendations: vi.fn(),
	},
	schedulerMock: {
		useScheduledJobs: vi.fn(),
	},
	hubMock: vi.fn(),
}));

vi.mock("@/features/model-fit/queries/useModelFit", () => hooksMock);
vi.mock("@/features/scheduler/queries/useScheduler", () => schedulerMock);
vi.mock("@/features/model-fit/hooks/useModelFitSchedulerEvents", () => ({ useModelFitSchedulerEvents: hubMock }));

import { ModelRecommendationsPage } from "@/features/model-fit/pages/ModelRecommendationsPage";

function modelFitJob(overrides: Partial<ScheduledJob> = {}): ScheduledJob {
	return {
		id: "job-mf",
		templateId: "model-recommendation-check",
		displayName: "Model recommendation check",
		description: "",
		enabled: true,
		scheduleKind: "Cron",
		cronExpression: "0 0 4 * * ?",
		intervalSeconds: null,
		repeatCount: null,
		startAtUtc: null,
		endAtUtc: null,
		timeZoneId: "UTC",
		misfirePolicy: "Smart",
		preventOverlap: true,
		maxRuntimeSeconds: null,
		hasParameters: true,
		createdBy: "User",
		createdAtUtc: 1000,
		updatedAtUtc: 2000,
		disabledAtUtc: null,
		deletedAtUtc: null,
		...overrides,
	};
}

function makeRefreshMutation() {
	return { mutate: vi.fn(), isPending: false, error: null };
}

function makeQuery<T>(data: T) {
	return { data, isLoading: false, error: null };
}

function installJsdomEnvironmentMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			onchange: null,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			dispatchEvent: vi.fn(),
		})),
	});
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { ready: Promise.resolve(), addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
}

function renderPage() {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	return render(
		<MantineProvider>
			<QueryClientProvider client={queryClient}>
				<ModelRecommendationsPage />
			</QueryClientProvider>
		</MantineProvider>,
	);
}

const noCacheView = {
	hasCache: false,
	snapshotId: null,
	status: null,
	sourceImageId: null,
	useCase: null,
	providerName: null,
	lastRefreshedAtUtc: null,
	recommendations: [],
};

const populatedView = {
	hasCache: true,
	snapshotId: "snap-1",
	status: "Succeeded",
	sourceImageId: "llmfit-recommender-0-9-30",
	useCase: "coding",
	providerName: "ollama",
	lastRefreshedAtUtc: 1_700_000_000_000,
	recommendations: [
		{
			rank: 1,
			modelName: "llama3.1:8b",
			providerModelName: "llama3.1:8b",
			score: 90,
			fitLevel: "Good",
			runMode: "GPU",
			quantization: "Q5_K_M",
			estimatedTokensPerSecond: 42,
			requiredRamMb: 8192,
			requiredVramMb: null,
			contextTokens: 8192,
			isInstalled: true,
			pullModelName: null,
			releaseDate: null,
		},
	],
};

describe("ModelRecommendationsPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		useModelFitManagementStore.setState({ useCase: "coding" });
		hooksMock.useLatestRecommendations.mockReturnValue(makeQuery(noCacheView));
		hooksMock.useApprovedImages.mockReturnValue(makeQuery([]));
		hooksMock.useRefreshRecommendations.mockReturnValue(makeRefreshMutation());
		schedulerMock.useScheduledJobs.mockReturnValue(makeQuery([modelFitJob()]));
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("mounts the model-fit scheduler-event hook for live updates", () => {
		renderPage();

		expect(hubMock).toHaveBeenCalled();
	});

	it("shows the no-cache empty state when hasCache is false", () => {
		renderPage();

		expect(screen.getByTestId("model-fit-no-cache")).toBeTruthy();
		expect(screen.queryByTestId("model-fit-recommendations-table")).toBeNull();
	});

	it("renders the ranked recommendation list when a cached snapshot exists", () => {
		hooksMock.useLatestRecommendations.mockReturnValue(makeQuery(populatedView));

		renderPage();

		expect(screen.getByTestId("model-fit-snapshot")).toBeTruthy();
		expect(screen.getByTestId("model-fit-recommendations-table")).toBeTruthy();
		expect(screen.getByTestId("model-fit-recommendation-row-1")).toBeTruthy();
	});

	it("surfaces a failed/stale diagnostics state via the load error", () => {
		hooksMock.useLatestRecommendations.mockReturnValue({
			data: undefined,
			isLoading: false,
			error: new Error("boom"),
		});

		renderPage();

		expect(screen.getByTestId("model-fit-recommendations-error")).toBeTruthy();
	});

	it("fires the existing model-recommendation-check job when Refresh now is clicked", () => {
		const refreshMutation = makeRefreshMutation();
		hooksMock.useRefreshRecommendations.mockReturnValue(refreshMutation);

		renderPage();

		const button = screen.getByTestId("model-fit-refresh-button") as HTMLButtonElement;
		expect(button.disabled).toBe(false);

		fireEvent.click(button);

		// Refresh now forwards the currently-selected use case (default "coding") alongside the job id.
		expect(refreshMutation.mutate).toHaveBeenCalledWith(
			{ scheduledJobId: "job-mf", useCase: "coding", limit: 500 },
			{ onError: expect.any(Function) },
		);
	});

	it("sends the selected use case (general) when Refresh now is clicked after switching the dropdown", () => {
		const refreshMutation = makeRefreshMutation();
		hooksMock.useRefreshRecommendations.mockReturnValue(refreshMutation);
		// Operator switched the use-case dropdown to general (the store drives both the query and the refresh override).
		useModelFitManagementStore.setState({ useCase: "general" });

		renderPage();

		fireEvent.click(screen.getByTestId("model-fit-refresh-button"));

		expect(refreshMutation.mutate).toHaveBeenCalledWith(
			{ scheduledJobId: "job-mf", useCase: "general", limit: 500 },
			{ onError: expect.any(Function) },
		);
	});

	it("disables Refresh now and shows guidance when no model-recommendation-check job exists", () => {
		schedulerMock.useScheduledJobs.mockReturnValue(makeQuery([]));

		renderPage();

		const button = screen.getByTestId("model-fit-refresh-button") as HTMLButtonElement;
		expect(button.disabled).toBe(true);
		expect(screen.getByTestId("model-fit-no-job-guidance")).toBeTruthy();
	});

	it("prefers an enabled model-recommendation-check job over a disabled one", () => {
		const refreshMutation = makeRefreshMutation();
		hooksMock.useRefreshRecommendations.mockReturnValue(refreshMutation);
		schedulerMock.useScheduledJobs.mockReturnValue(
			makeQuery([modelFitJob({ id: "job-disabled", enabled: false }), modelFitJob({ id: "job-enabled", enabled: true })]),
		);

		renderPage();

		fireEvent.click(screen.getByTestId("model-fit-refresh-button"));

		expect(refreshMutation.mutate).toHaveBeenCalledWith(
			{ scheduledJobId: "job-enabled", useCase: "coding", limit: 500 },
			{ onError: expect.any(Function) },
		);
	});

	it("ignores scheduler jobs of other templates when gating refresh", () => {
		schedulerMock.useScheduledJobs.mockReturnValue(makeQuery([modelFitJob({ id: "other", templateId: "agent-cleanup" })]));

		renderPage();

		const button = screen.getByTestId("model-fit-refresh-button") as HTMLButtonElement;
		expect(button.disabled).toBe(true);
	});
});
