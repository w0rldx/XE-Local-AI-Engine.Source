// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
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

vi.mock("@/core/loaded-models/queries/useRunningModels", () => runningMock);
vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: confirmMock }) }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

import { LoadedModelsPage } from "@/features/loaded-models/pages/LoadedModelsPage";
import { installJsdomEnvironmentMocks, testMantineTheme } from "@/test/MantineTestRender";

function makeQuery<T>(data: T, overrides: Record<string, unknown> = {}) {
	return { data, isLoading: false, error: null, ...overrides };
}

function makeEjectMutation(overrides: Record<string, unknown> = {}) {
	return {
		mutateAsync: vi.fn().mockResolvedValue({ modelName: "running-a", role: "chat", outcome: "ejected" }),
		isPending: false,
		variables: undefined,
		...overrides,
	};
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
	const runningModel = {
		modelName: "running-a",
		role: "chat",
		isResponsive: true,
		detail: "",
		effectiveContextTokens: null,
		expertsOffloaded: false,
	};

	it("renders the llama.cpp running-models table", () => {
		runningMock.useRunningModels.mockReturnValue(makeQuery([runningModel]));

		renderPage();

		expect(screen.getByTestId("loaded-models-llamacpp-table")).toBeTruthy();
		expect(screen.getByTestId("loaded-models-llamacpp-row-running-a")).toBeTruthy();
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

		// The eject confirms first (async), then dispatches a graceful (force=false) eject.
		await waitFor(() => expect(ejectRunning.mutateAsync).toHaveBeenCalled());
		expect(confirmMock).toHaveBeenCalled();
		expect(ejectRunning.mutateAsync).toHaveBeenCalledWith({ modelName: "running-a", role: "chat", force: false });
	});

	it("surfaces a success toast when the graceful eject reports 'ejected'", async () => {
		runningMock.useRunningModels.mockReturnValue(makeQuery([runningModel]));

		renderPage();
		fireEvent.click(screen.getByTestId("loaded-models-llamacpp-eject-running-a"));

		await waitFor(() => expect(toastMock.success).toHaveBeenCalled());
	});

	it("toasts a failed eject and lets no rejection escape the click", async () => {
		const ejectRunning = makeEjectMutation({ mutateAsync: vi.fn().mockRejectedValue(new Error("boom")) });
		runningMock.useRunningModels.mockReturnValue(makeQuery([runningModel]));
		runningMock.useEjectRunningModel.mockReturnValue(ejectRunning);

		renderPage();
		fireEvent.click(screen.getByTestId("loaded-models-llamacpp-eject-running-a"));

		await waitFor(() => expect(toastMock.error).toHaveBeenCalled());
	});

	it("warns and offers a force eject when the graceful eject times out still busy", async () => {
		const mutateAsync = vi
			.fn()
			.mockResolvedValueOnce({ modelName: "running-a", role: "chat", outcome: "timed_out_still_busy" })
			.mockResolvedValueOnce({ modelName: "running-a", role: "chat", outcome: "forced" });
		runningMock.useRunningModels.mockReturnValue(makeQuery([runningModel]));
		runningMock.useEjectRunningModel.mockReturnValue(makeEjectMutation({ mutateAsync }));

		renderPage();
		fireEvent.click(screen.getByTestId("loaded-models-llamacpp-eject-running-a"));

		// A warning toast fires and the force-eject confirm is offered; confirming it re-ejects with force=true.
		await waitFor(() => expect(mutateAsync).toHaveBeenCalledTimes(2));
		expect(toastMock.warning).toHaveBeenCalled();
		expect(confirmMock).toHaveBeenCalledTimes(2);
		expect(mutateAsync).toHaveBeenNthCalledWith(2, { modelName: "running-a", role: "chat", force: true });
		await waitFor(() => expect(toastMock.info).toHaveBeenCalled());
	});

	it("opens no force confirmation once the page has gone", async () => {
		let answer!: (result: unknown) => void;
		const mutateAsync = vi
			.fn()
			.mockReturnValueOnce(
				new Promise((resolve) => {
					answer = resolve;
				}),
			)
			.mockResolvedValue({ modelName: "running-a", role: "chat", outcome: "forced" });
		runningMock.useRunningModels.mockReturnValue(makeQuery([runningModel]));
		runningMock.useEjectRunningModel.mockReturnValue(makeEjectMutation({ mutateAsync }));

		const { unmount } = renderPage();
		fireEvent.click(screen.getByTestId("loaded-models-llamacpp-eject-running-a"));
		await waitFor(() => expect(mutateAsync).toHaveBeenCalled());
		unmount();
		await act(async () => answer({ modelName: "running-a", role: "chat", outcome: "timed_out_still_busy" }));

		// The warning proves the continuation ran; only the dialog is skipped.
		await waitFor(() => expect(toastMock.warning).toHaveBeenCalled());
		expect(confirmMock).toHaveBeenCalledTimes(1);
	});
});
