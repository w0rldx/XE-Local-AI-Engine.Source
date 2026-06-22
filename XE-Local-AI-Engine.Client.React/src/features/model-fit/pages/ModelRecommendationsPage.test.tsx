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
vi.mock("@tanstack/react-router", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@tanstack/react-router")>();
	return {
		...actual,
		useNavigate: () => navigateMock,
	};
});

// The advisor consumes the recommendation/hardware/refresh hooks. A recommendation-row download is handed off to the
// Model Management feature: it calls that feature's useStartGgufDownload and marks the model in the SHARED GGUF store
// (the real store is used here — not mocked — so the test can assert the in-flight set the progress panel reads). The
// browse/llama.cpp/HF-token/running-models hooks were relocated and are no longer referenced by this page.
const { hooksMock, ggufMock, schedulerMock, hubMock } = vi.hoisted(() => ({
	hooksMock: {
		useLatestRecommendations: vi.fn(),
		useRefreshRecommendations: vi.fn(),
		useHardwareProfile: vi.fn(),
	},
	ggufMock: {
		useStartGgufDownload: vi.fn(),
	},
	schedulerMock: {
		useScheduledJobs: vi.fn(),
	},
	hubMock: vi.fn(),
}));

vi.mock("@/features/model-fit/queries/useModelFit", () => hooksMock);
vi.mock("@/features/models/queries/useGgufDownload", () => ggufMock);
vi.mock("@/features/scheduler/queries/useScheduler", () => schedulerMock);
vi.mock("@/features/model-fit/hooks/useModelFitSchedulerEvents", () => ({ useModelFitSchedulerEvents: hubMock }));

import { ModelRecommendationsPage } from "@/features/model-fit/pages/ModelRecommendationsPage";
import { useGgufBrowseStore } from "@/features/models/stores/GgufBrowseStore";

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

function makeMutation(overrides: Record<string, unknown> = {}) {
	return { mutate: vi.fn(), isPending: false, error: null, variables: undefined, ...overrides };
}

function makeQuery<T>(data: T, overrides: Record<string, unknown> = {}) {
	return { data, isLoading: false, isFetching: false, error: null, refetch: vi.fn().mockResolvedValue({}), ...overrides };
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
	useCase: null,
	lastRefreshedAtUtc: null,
	recommendations: [],
};

const populatedView = {
	hasCache: true,
	snapshotId: "snap-1",
	status: "Succeeded",
	useCase: "coding",
	lastRefreshedAtUtc: 1_700_000_000_000,
	recommendations: [
		{
			rank: 1,
			modelName: "llama3.1:8b",
			providerModelName: "llama3.1:8b",
			score: 90,
			fitLevel: "GPU",
			runMode: "GPU",
			quantization: "Q5_K_M",
			estimatedTokensPerSecond: 42,
			requiredRamMb: 8192,
			requiredVramMb: 6144,
			contextTokens: 8192,
			isInstalled: false,
			pullModelName: "unsloth/llama-3.1-8b-gguf",
			releaseDate: null,
		},
	],
};

const hardwareProfile = {
	totalRamBytes: 34_359_738_368,
	availableRamBytes: 17_179_869_184,
	vramBytes: 8_589_934_592,
	vramKnown: true,
	gpuVendor: "nvidia" as const,
	gpuAccelAvailable: true,
	cpuCores: 16,
	freeDiskBytes: 500_000_000_000,
};

describe("ModelRecommendationsPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		useModelFitManagementStore.setState({ useCase: "coding" });
		// Reset the shared GGUF in-flight set so the hand-off assertion starts from empty.
		useGgufBrowseStore.setState({ browseQuery: "", inFlightDownloads: [] });
		hooksMock.useLatestRecommendations.mockReturnValue(makeQuery(noCacheView));
		hooksMock.useRefreshRecommendations.mockReturnValue(makeMutation());
		hooksMock.useHardwareProfile.mockReturnValue(makeQuery(hardwareProfile));
		ggufMock.useStartGgufDownload.mockReturnValue(makeMutation());
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

	it("renders the hardware-profile card with RAM / VRAM / GPU vendor / CPU cores", () => {
		renderPage();

		expect(screen.getByTestId("model-fit-hardware-card")).toBeTruthy();
		expect(screen.getByTestId("model-fit-hardware-total-ram").textContent).toContain("32.0 GB");
		expect(screen.getByTestId("model-fit-hardware-vram").textContent).toContain("8.0 GB");
		expect(screen.getByTestId("model-fit-hardware-gpu-vendor")).toBeTruthy();
		expect(screen.getByTestId("model-fit-hardware-cpu-cores").textContent).toContain("16");
	});

	it("shows the CPU-mode badge and 'VRAM unknown' when GPU acceleration is unavailable", () => {
		hooksMock.useHardwareProfile.mockReturnValue(
			makeQuery({ ...hardwareProfile, vramBytes: null, vramKnown: false, gpuVendor: "none" as const, gpuAccelAvailable: false }),
		);

		renderPage();

		expect(screen.getByTestId("model-fit-hardware-cpu-mode-badge")).toBeTruthy();
		expect(screen.getByTestId("model-fit-hardware-vram").textContent).toContain("VRAM unknown");
	});

	it("re-probes hardware when 'Refresh hardware' is clicked", () => {
		const refetch = vi.fn().mockResolvedValue({});
		hooksMock.useHardwareProfile.mockReturnValue(makeQuery(hardwareProfile, { refetch }));

		renderPage();

		fireEvent.click(screen.getByTestId("model-fit-hardware-refresh"));

		expect(refetch).toHaveBeenCalled();
	});

	it("shows the no-cache empty state when hasCache is false", () => {
		renderPage();

		expect(screen.getByTestId("model-fit-no-cache")).toBeTruthy();
		expect(screen.queryByTestId("model-fit-recommendations-table")).toBeNull();
	});

	it("renders the ranked recommendation list with file/quant/fit estimate when a cached snapshot exists", () => {
		hooksMock.useLatestRecommendations.mockReturnValue(makeQuery(populatedView));

		renderPage();

		expect(screen.getByTestId("model-fit-snapshot")).toBeTruthy();
		expect(screen.getByTestId("model-fit-recommendations-table")).toBeTruthy();
		expect(screen.getByTestId("model-fit-recommendation-row-1")).toBeTruthy();
	});

	it("triggers a GGUF download from a recommendation row via the Model Management feature's hook", () => {
		const download = makeMutation();
		ggufMock.useStartGgufDownload.mockReturnValue(download);
		hooksMock.useLatestRecommendations.mockReturnValue(makeQuery(populatedView));

		renderPage();

		fireEvent.click(screen.getByTestId("model-fit-download-button-1"));

		expect(download.mutate).toHaveBeenCalledWith(
			{ repoId: "unsloth/llama-3.1-8b-gguf", fileName: undefined, quant: "Q5_K_M" },
			expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
		);
	});

	it("hands the download off to Model Management by marking the resolved model in the shared in-flight store", () => {
		// A mutate that resolves with the backend-resolved model name (distinct from the repo id) so the test proves the
		// store is keyed on the RESPONSE model name — exactly what the Model Management download-progress panel reads.
		const download = makeMutation({
			mutate: vi.fn((_variables, options?: { onSuccess?: (response: unknown) => void }) =>
				options?.onSuccess?.({ modelName: "hf.co/unsloth/llama-3.1-8b-gguf:Q5_K_M", alreadyInFlight: false }),
			),
		});
		ggufMock.useStartGgufDownload.mockReturnValue(download);
		hooksMock.useLatestRecommendations.mockReturnValue(makeQuery(populatedView));

		renderPage();

		// The shared in-flight set starts empty (reset in beforeEach).
		expect(useGgufBrowseStore.getState().inFlightDownloads).toEqual([]);

		fireEvent.click(screen.getByTestId("model-fit-download-button-1"));

		// On success the resolved model name is marked in the shared store, so the Model Management panel would show it.
		expect(useGgufBrowseStore.getState().inFlightDownloads).toEqual(["hf.co/unsloth/llama-3.1-8b-gguf:Q5_K_M"]);
	});

	it("surfaces a load error for the recommendations list", () => {
		hooksMock.useLatestRecommendations.mockReturnValue(makeQuery(undefined, { error: new Error("boom") }));

		renderPage();

		expect(screen.getByTestId("model-fit-recommendations-error")).toBeTruthy();
	});

	it("fires the existing model-recommendation-check job when Refresh now is clicked", () => {
		const refreshMutation = makeMutation();
		hooksMock.useRefreshRecommendations.mockReturnValue(refreshMutation);

		renderPage();

		const button = screen.getByTestId("model-fit-refresh-button") as HTMLButtonElement;
		expect(button.disabled).toBe(false);

		fireEvent.click(button);

		expect(refreshMutation.mutate).toHaveBeenCalledWith(
			{ scheduledJobId: "job-mf", useCase: "coding", limit: 500 },
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

	it("no longer renders the relocated GGUF browse, llama.cpp, HF token, or running-models panels", () => {
		hooksMock.useLatestRecommendations.mockReturnValue(makeQuery(populatedView));

		renderPage();

		// These panels moved to Model Management / Node Settings / Loaded Models — the advisor is now slim.
		expect(screen.queryByTestId("model-fit-browse-card")).toBeNull();
		expect(screen.queryByTestId("model-fit-download-card")).toBeNull();
		expect(screen.queryByTestId("model-fit-running-card")).toBeNull();
		expect(screen.queryByTestId("model-fit-llamacpp-card")).toBeNull();
		expect(screen.queryByTestId("model-fit-hf-token-card")).toBeNull();
	});
});
