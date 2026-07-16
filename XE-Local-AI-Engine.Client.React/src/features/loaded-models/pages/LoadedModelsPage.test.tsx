// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

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

const { hooksMock, runningMock, confirmMock, toastMock } = vi.hoisted(() => ({
	hooksMock: {
		useLoadedModels: vi.fn(),
		useEjectModel: vi.fn(),
	},
	// The llama.cpp running-models section is a DIFFERENT runtime, backed by its own query module.
	runningMock: {
		useRunningModels: vi.fn(),
		useEjectRunningModel: vi.fn(),
	},
	confirmMock: vi.fn(),
	toastMock: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));

vi.mock("@/features/loaded-models/queries/useLoadedModels", () => hooksMock);
vi.mock("@/features/loaded-models/queries/useRunningModels", () => runningMock);
vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: confirmMock }) }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

import { LoadedModelsPage } from "@/features/loaded-models/pages/LoadedModelsPage";

function makeQuery<T>(data: T, overrides: Record<string, unknown> = {}) {
	return { data, isLoading: false, error: null, ...overrides };
}

function makeEjectMutation(overrides: Record<string, unknown> = {}) {
	return { mutate: vi.fn(), isPending: false, variables: undefined, ...overrides };
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
}

function renderPage() {
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	return render(
		<MantineProvider>
			<QueryClientProvider client={queryClient}>
				<LoadedModelsPage />
			</QueryClientProvider>
		</MantineProvider>,
	);
}

const availableSnapshot = {
	isAvailable: true,
	error: null,
	models: [
		{ modelName: "llama3.1:8b", sizeBytes: 8_589_934_592, sizeVramBytes: 4_294_967_296, expiresAtUtc: null },
		{ modelName: "qwen2.5:3b", sizeBytes: 3_221_225_472, sizeVramBytes: null, expiresAtUtc: null },
	],
};

describe("LoadedModelsPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		hooksMock.useLoadedModels.mockReturnValue(makeQuery(availableSnapshot));
		hooksMock.useEjectModel.mockReturnValue(makeEjectMutation());
		runningMock.useRunningModels.mockReturnValue(makeQuery([]));
		runningMock.useEjectRunningModel.mockReturnValue(makeEjectMutation());
		confirmMock.mockResolvedValue(true);
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders a row per loaded model with an eject button", () => {
		renderPage();

		expect(screen.getByTestId("loaded-models-table")).toBeTruthy();
		expect(screen.getByTestId("loaded-models-row-llama3.1:8b")).toBeTruthy();
		expect(screen.getByTestId("loaded-models-eject-llama3.1:8b")).toBeTruthy();
		expect(screen.getByTestId("loaded-models-row-qwen2.5:3b")).toBeTruthy();
	});

	it("shows the empty state when the runtime is available but holds no models", () => {
		hooksMock.useLoadedModels.mockReturnValue(makeQuery({ isAvailable: true, error: null, models: [] }));

		renderPage();

		expect(screen.getByTestId("loaded-models-empty")).toBeTruthy();
		expect(screen.queryByTestId("loaded-models-table")).toBeNull();
	});

	it("shows a neutral absent-provider state (not the raw error) when Ollama is unreachable", () => {
		// Ollama is an optional secondary provider, deliberately absent on the desktop default. An unreachable
		// provider is an expected empty state, not an error: the neutral line renders, the raw connection-refused
		// reason is NOT surfaced as an alarming banner, and the table is hidden.
		hooksMock.useLoadedModels.mockReturnValue(makeQuery({ isAvailable: false, error: "Provider unreachable", models: [] }));

		renderPage();

		const unavailable = screen.getByTestId("loaded-models-unavailable");
		expect(unavailable.textContent).toContain("optional secondary provider");
		expect(unavailable.textContent).not.toContain("Provider unreachable");
		// The neutral state is not the red error alert.
		expect(screen.queryByTestId("loaded-models-error")).toBeNull();
		expect(screen.queryByTestId("loaded-models-table")).toBeNull();
	});

	it("surfaces a load error via the error alert", () => {
		hooksMock.useLoadedModels.mockReturnValue(makeQuery(undefined, { error: new Error("boom") }));

		renderPage();

		expect(screen.getByTestId("loaded-models-error")).toBeTruthy();
	});

	it("ejects a model after the confirm dialog is accepted", async () => {
		const ejectMutation = makeEjectMutation();
		hooksMock.useEjectModel.mockReturnValue(ejectMutation);

		renderPage();

		fireEvent.click(screen.getByTestId("loaded-models-eject-llama3.1:8b"));

		await waitFor(() => expect(ejectMutation.mutate).toHaveBeenCalled());
		expect(ejectMutation.mutate.mock.calls[0]?.[0]).toBe("llama3.1:8b");
	});

	it("does not eject when the confirm dialog is cancelled", async () => {
		const ejectMutation = makeEjectMutation();
		hooksMock.useEjectModel.mockReturnValue(ejectMutation);
		confirmMock.mockResolvedValue(false);

		renderPage();

		fireEvent.click(screen.getByTestId("loaded-models-eject-llama3.1:8b"));

		await waitFor(() => expect(confirmMock).toHaveBeenCalled());
		expect(ejectMutation.mutate).not.toHaveBeenCalled();
	});

	// llama.cpp running-models section (relocated from the model-fit advisor) — a DIFFERENT runtime rendered as its
	// own labeled section alongside the Ollama in-memory table.
	const runningModel = { modelName: "running-a", role: "chat", isResponsive: true, detail: "" };

	it("renders the llama.cpp running-models section as a second runtime table", () => {
		runningMock.useRunningModels.mockReturnValue(makeQuery([runningModel]));

		renderPage();

		// Both runtimes show: the Ollama in-memory table and the llama.cpp running-models table.
		expect(screen.getByTestId("loaded-models-table")).toBeTruthy();
		expect(screen.getByTestId("model-fit-running-table")).toBeTruthy();
		expect(screen.getByTestId("model-fit-running-row-running-a")).toBeTruthy();
	});

	it("shows the empty state for the llama.cpp section when no processes are running", () => {
		renderPage();

		expect(screen.getByTestId("model-fit-running-empty")).toBeTruthy();
	});

	it("ejects a llama.cpp running model through its own eject mutation", () => {
		const ejectRunning = makeEjectMutation();
		runningMock.useRunningModels.mockReturnValue(makeQuery([runningModel]));
		runningMock.useEjectRunningModel.mockReturnValue(ejectRunning);

		renderPage();

		fireEvent.click(screen.getByTestId("model-fit-eject-button-running-a"));

		expect(ejectRunning.mutate).toHaveBeenCalledWith(
			{ modelName: "running-a", role: "chat" },
			expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
		);
	});
});
