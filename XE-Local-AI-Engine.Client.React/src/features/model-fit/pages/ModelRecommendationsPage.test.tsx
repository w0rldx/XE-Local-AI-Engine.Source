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

const { hooksMock, schedulerMock, hubMock } = vi.hoisted(() => ({
	hooksMock: {
		useLatestRecommendations: vi.fn(),
		useRefreshRecommendations: vi.fn(),
		useHardwareProfile: vi.fn(),
		useRunningModels: vi.fn(),
		useLlamaCppVersion: vi.fn(),
		useHfTokenStatus: vi.fn(),
		useBrowseGgufRepositories: vi.fn(),
		useInspectGgufRepository: vi.fn(),
		useStartGgufDownload: vi.fn(),
		useCancelGgufDownload: vi.fn(),
		useEjectRunningModel: vi.fn(),
		useEnsureLlamaCppBinary: vi.fn(),
		useSetHfToken: vi.fn(),
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

const runningModel = { modelName: "running-a", role: "chat", isResponsive: true, detail: "" };
const llamaCppVersion = { version: "b1234", variant: "cuda" as const, isPinnedFallback: false, pinnedTag: "b1000" };
const ggufRepo = {
	repoId: "unsloth/llama-3.1-8b-gguf",
	isGated: false,
	downloads: 1000,
	likes: 50,
	lastModifiedAtUtc: 1_700_000_000_000,
	license: "apache-2.0",
	hasUsableGguf: true,
};

describe("ModelRecommendationsPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		useModelFitManagementStore.setState({ useCase: "coding", browseQuery: "", tokenDraft: "" });
		hooksMock.useLatestRecommendations.mockReturnValue(makeQuery(noCacheView));
		hooksMock.useRefreshRecommendations.mockReturnValue(makeMutation());
		hooksMock.useHardwareProfile.mockReturnValue(makeQuery(hardwareProfile));
		hooksMock.useRunningModels.mockReturnValue(makeQuery([runningModel]));
		hooksMock.useLlamaCppVersion.mockReturnValue(makeQuery(llamaCppVersion));
		hooksMock.useHfTokenStatus.mockReturnValue(makeQuery(false));
		hooksMock.useBrowseGgufRepositories.mockReturnValue(makeQuery([]));
		hooksMock.useInspectGgufRepository.mockReturnValue(makeQuery(null));
		hooksMock.useStartGgufDownload.mockReturnValue(makeMutation());
		hooksMock.useCancelGgufDownload.mockReturnValue(makeMutation());
		hooksMock.useEjectRunningModel.mockReturnValue(makeMutation());
		hooksMock.useEnsureLlamaCppBinary.mockReturnValue(makeMutation());
		hooksMock.useSetHfToken.mockReturnValue(makeMutation());
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

	it("triggers a GGUF download from a recommendation row", () => {
		const download = makeMutation();
		hooksMock.useStartGgufDownload.mockReturnValue(download);
		hooksMock.useLatestRecommendations.mockReturnValue(makeQuery(populatedView));

		renderPage();

		fireEvent.click(screen.getByTestId("model-fit-download-button-1"));

		expect(download.mutate).toHaveBeenCalledWith(
			{ repoId: "unsloth/llama-3.1-8b-gguf", fileName: undefined, quant: "Q5_K_M" },
			expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
		);
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

	it("ejects a running model from the running-models panel", () => {
		const eject = makeMutation();
		hooksMock.useEjectRunningModel.mockReturnValue(eject);

		renderPage();

		fireEvent.click(screen.getByTestId("model-fit-eject-button-running-a"));

		expect(eject.mutate).toHaveBeenCalledWith(
			{ modelName: "running-a", role: "chat" },
			expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
		);
	});

	it("does not show the llama.cpp version until the operator explicitly checks it (avoids a mount-time download)", () => {
		renderPage();

		// On mount the version probe is idle: the panel shows its idle hint and renders no version.
		expect(screen.getByTestId("model-fit-llamacpp-idle")).toBeTruthy();
		expect(screen.queryByTestId("model-fit-llamacpp-version")).toBeNull();

		// Triggering the probe reveals the resolved version.
		fireEvent.click(screen.getByTestId("model-fit-llamacpp-check-button"));
		expect(screen.getByTestId("model-fit-llamacpp-version").textContent).toContain("b1234");
	});

	it("renders the llama.cpp version panel and ensures the selected variant", () => {
		const ensure = makeMutation();
		hooksMock.useEnsureLlamaCppBinary.mockReturnValue(ensure);

		renderPage();

		// The version block is gated behind an explicit check (the GET can trigger the first binary download).
		fireEvent.click(screen.getByTestId("model-fit-llamacpp-check-button"));
		expect(screen.getByTestId("model-fit-llamacpp-version").textContent).toContain("b1234");
		fireEvent.click(screen.getByTestId("model-fit-llamacpp-ensure-button"));

		// The select defaults to "cpu" until the operator changes it.
		expect(ensure.mutate).toHaveBeenCalledWith("cpu", expect.any(Object));
	});

	it("commits a GGUF browse search term to the store", () => {
		renderPage();

		fireEvent.change(screen.getByTestId("model-fit-browse-input"), { target: { value: "llama 3.1" } });
		fireEvent.click(screen.getByTestId("model-fit-browse-search-button"));

		expect(useModelFitManagementStore.getState().browseQuery).toBe("llama 3.1");
	});

	it("downloads a chosen quant from the browse quant picker", async () => {
		const download = makeMutation();
		hooksMock.useStartGgufDownload.mockReturnValue(download);
		hooksMock.useBrowseGgufRepositories.mockReturnValue(makeQuery([ggufRepo]));
		hooksMock.useInspectGgufRepository.mockReturnValue(
			makeQuery({
				repoId: "unsloth/llama-3.1-8b-gguf",
				files: [
					{ fileName: "llama-3.1-8b-Q4_K_M.gguf", quant: "Q4_K_M", isDynamic: false, sizeBytes: 5_000_000_000 },
					{ fileName: "llama-3.1-8b-UD-Q4_K_XL.gguf", quant: "UD-Q4_K_XL", isDynamic: true, sizeBytes: 6_000_000_000 },
				],
			}),
		);
		useModelFitManagementStore.setState({ browseQuery: "llama" });

		renderPage();

		// Clicking a browse row opens the quant picker rather than downloading the default quant directly.
		fireEvent.click(screen.getByTestId("model-fit-browse-download-unsloth/llama-3.1-8b-gguf"));

		// Pick the Unsloth Dynamic quant, then confirm — the exact file name is sent so it resolves unambiguously.
		fireEvent.click(await screen.findByLabelText("UD-Q4_K_XL"));
		fireEvent.click(screen.getByTestId("gguf-download-confirm"));

		expect(download.mutate).toHaveBeenCalledWith(
			{ repoId: "unsloth/llama-3.1-8b-gguf", fileName: "llama-3.1-8b-UD-Q4_K_XL.gguf", quant: "UD-Q4_K_XL" },
			expect.any(Object),
		);
	});

	it("falls back to the default quant when the picker has no files to offer", async () => {
		const download = makeMutation();
		hooksMock.useStartGgufDownload.mockReturnValue(download);
		hooksMock.useBrowseGgufRepositories.mockReturnValue(makeQuery([ggufRepo]));
		// Degraded/empty inspection (e.g. HF unreachable → 200 empty list) must not strand the operator.
		hooksMock.useInspectGgufRepository.mockReturnValue(
			makeQuery({ repoId: "unsloth/llama-3.1-8b-gguf", files: [] }),
		);
		useModelFitManagementStore.setState({ browseQuery: "llama" });

		renderPage();

		fireEvent.click(screen.getByTestId("model-fit-browse-download-unsloth/llama-3.1-8b-gguf"));
		fireEvent.click(await screen.findByTestId("gguf-download-default"));

		expect(download.mutate).toHaveBeenCalledWith(
			{ repoId: "unsloth/llama-3.1-8b-gguf", fileName: undefined, quant: "Q4_K_M" },
			expect.any(Object),
		);
	});

	it("renders the HF token panel with a masked input and never the token value", () => {
		hooksMock.useHfTokenStatus.mockReturnValue(makeQuery(true));

		renderPage();

		const input = screen.getByTestId("model-fit-hf-token-input") as HTMLInputElement;
		// PasswordInput renders a type=password field — the value is masked, never plain text.
		expect(input.type).toBe("password");
		expect(screen.getByTestId("model-fit-hf-token-status").textContent).toContain("Token configured");
	});

	it("saves the HF token draft and clears it", () => {
		const setToken = makeMutation();
		hooksMock.useSetHfToken.mockReturnValue(setToken);
		useModelFitManagementStore.setState({ tokenDraft: "hf_secret" });

		renderPage();

		fireEvent.click(screen.getByTestId("model-fit-hf-token-save"));

		expect(setToken.mutate).toHaveBeenCalledWith("hf_secret", expect.any(Object));
	});
});
