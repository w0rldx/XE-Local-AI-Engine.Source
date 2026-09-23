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

const { runningMock, confirmMock, toastMock } = vi.hoisted(() => ({
	runningMock: {
		useRunningModels: vi.fn(),
		useEjectRunningModel: vi.fn(),
	},
	confirmMock: vi.fn(),
	toastMock: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() },
}));

vi.mock("@/features/loaded-models/queries/useRunningModels", () => runningMock);
vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: confirmMock }) }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

import { LoadedModelsPage } from "@/features/loaded-models/pages/LoadedModelsPage";
import { testMantineTheme } from "@/test/MantineTestRender";

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
		<MantineProvider env="test" theme={testMantineTheme}>
			<QueryClientProvider client={queryClient}>
				<LoadedModelsPage />
			</QueryClientProvider>
		</MantineProvider>,
	);
}

describe("LoadedModelsPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		runningMock.useRunningModels.mockReturnValue(makeQuery([]));
		runningMock.useEjectRunningModel.mockReturnValue(makeEjectMutation());
		confirmMock.mockResolvedValue(true);
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	// The page lists the llama.cpp runtime only (relocated from the model-fit advisor).
	const runningModel = { modelName: "running-a", role: "chat", isResponsive: true, detail: "" };

	it("renders the llama.cpp running-models table and no Ollama section", () => {
		runningMock.useRunningModels.mockReturnValue(makeQuery([runningModel]));

		renderPage();

		expect(screen.getByTestId("loaded-models-llamacpp-table")).toBeTruthy();
		expect(screen.getByTestId("loaded-models-llamacpp-row-running-a")).toBeTruthy();
		// The Ollama in-memory section was removed from this page; nothing Ollama-specific may render.
		expect(screen.queryByTestId("loaded-models-table")).toBeNull();
		expect(screen.queryByText(/ollama/i)).toBeNull();
	});

	it("shows the empty state for the llama.cpp section when no processes are running", () => {
		renderPage();

		expect(screen.getByTestId("loaded-models-llamacpp-empty")).toBeTruthy();
	});

	it("confirms then ejects a llama.cpp running model gracefully (force=false) through its own eject mutation", async () => {
		const ejectRunning = makeEjectMutation();
		runningMock.useRunningModels.mockReturnValue(makeQuery([runningModel]));
		runningMock.useEjectRunningModel.mockReturnValue(ejectRunning);

		renderPage();

		fireEvent.click(screen.getByTestId("loaded-models-llamacpp-eject-running-a"));

		// The eject now confirms first (async), then dispatches a graceful (force=false) eject.
		await waitFor(() => expect(ejectRunning.mutate).toHaveBeenCalled());
		expect(confirmMock).toHaveBeenCalled();
		expect(ejectRunning.mutate).toHaveBeenCalledWith(
			{ modelName: "running-a", role: "chat", force: false },
			expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
		);
	});

	it("surfaces a success toast when the graceful eject reports 'ejected'", async () => {
		const ejectRunning = makeEjectMutation();
		runningMock.useRunningModels.mockReturnValue(makeQuery([runningModel]));
		runningMock.useEjectRunningModel.mockReturnValue(ejectRunning);

		renderPage();
		fireEvent.click(screen.getByTestId("loaded-models-llamacpp-eject-running-a"));
		await waitFor(() => expect(ejectRunning.mutate).toHaveBeenCalled());

		// Drive the mutation's onSuccess callback with the backend outcome the page maps to a toast.
		const onSuccess = ejectRunning.mutate.mock.calls[0]?.[1]?.onSuccess as (result: {
			modelName: string;
			role: string;
			outcome: string;
		}) => void;
		onSuccess({ modelName: "running-a", role: "chat", outcome: "ejected" });

		expect(toastMock.success).toHaveBeenCalled();
	});

	it("warns and offers a force eject when the graceful eject times out still busy", async () => {
		const ejectRunning = makeEjectMutation();
		runningMock.useRunningModels.mockReturnValue(makeQuery([runningModel]));
		runningMock.useEjectRunningModel.mockReturnValue(ejectRunning);

		renderPage();
		fireEvent.click(screen.getByTestId("loaded-models-llamacpp-eject-running-a"));
		await waitFor(() => expect(ejectRunning.mutate).toHaveBeenCalled());
		confirmMock.mockClear();

		const onSuccess = ejectRunning.mutate.mock.calls[0]?.[1]?.onSuccess as (result: {
			modelName: string;
			role: string;
			outcome: string;
		}) => Promise<void>;
		await onSuccess({ modelName: "running-a", role: "chat", outcome: "timed_out_still_busy" });

		// A warning toast fires and the force-eject confirm is offered; confirming it re-ejects with force=true.
		expect(toastMock.warning).toHaveBeenCalled();
		await waitFor(() =>
			expect(ejectRunning.mutate).toHaveBeenCalledWith(
				{ modelName: "running-a", role: "chat", force: true },
				expect.objectContaining({ onSuccess: expect.any(Function), onError: expect.any(Function) }),
			),
		);
	});
});
