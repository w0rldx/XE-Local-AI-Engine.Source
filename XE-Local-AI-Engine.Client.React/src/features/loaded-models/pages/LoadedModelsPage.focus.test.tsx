// @vitest-environment jsdom

import { MantineProvider, Modal } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { type ReactNode, use } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { RunningModel } from "@/core/loaded-models/models/RunningModelsModels";

// The running list is a mutable value the test swaps to stand in for a residency push; the confirmation dialog is the
// real ConfirmProvider, because its focus return is what this file pins.
const { runningState } = vi.hoisted(() => ({
	runningState: { models: [] as RunningModel[], mutateAsync: vi.fn() },
}));

vi.mock("@/core/loaded-models/queries/useRunningModels", () => ({
	useRunningModels: () => ({ data: runningState.models, isLoading: false, error: null }),
	useEjectRunningModel: () => ({ mutateAsync: runningState.mutateAsync, isPending: false, variables: undefined }),
}));
vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() },
}));

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import type { ConfirmContextType } from "@/core/ui/models/Confirm";
import { LoadedModelsPage } from "@/features/loaded-models/pages/LoadedModelsPage";
import { installJsdomEnvironmentMocks, testMantineTheme } from "@/test/MantineTestRender";

function running(modelName: string): RunningModel {
	return {
		modelName,
		role: "chat",
		isResponsive: true,
		detail: "",
		detailCode: "responsive",
		isBusy: false,
		lastUsedUtc: null,
		isTransient: false,
		effectiveContextTokens: null,
		expertsOffloaded: false,
	};
}

const queryClient = new QueryClient();

// The real modal timing: testMantineTheme and env="test" both make a closed dialog unmount at once, which hides the
// window in which ConfirmProvider has already resolved but the dialog still holds focus.
const slowModalTheme = { components: { Modal: { defaultProps: { transitionProps: { duration: 30, exitDuration: 30 } } } } };

// withPage=false unmounts the page under a ConfirmProvider that stays mounted, as a route change does; the shell's
// focusable main landmark stays around the swap, as Layout's does. `beside` renders next to the page, under the provider.
function page(slowModal = false, withPage = true, beside: ReactNode = null) {
	return (
		<MantineProvider env={slowModal ? "default" : "test"} theme={slowModal ? slowModalTheme : testMantineTheme}>
			<QueryClientProvider client={queryClient}>
				<ConfirmProvider>
					<main id="main-content" tabIndex={-1}>
						{withPage ? <LoadedModelsPage /> : null}
					</main>
					{beside}
				</ConfirmProvider>
			</QueryClientProvider>
		</MantineProvider>
	);
}

// Hands the test the provider's confirm, as another flow on another part of the app would call it.
function CaptureConfirm({ into }: { readonly into: { confirm?: ConfirmContextType["confirm"] } }) {
	into.confirm = use(ConfirmContext)?.confirm;
	return null;
}

function result(modelName: string, outcome: string) {
	return { modelName, role: "chat", outcome };
}

function held(): { readonly promise: Promise<unknown>; readonly answer: (value: unknown) => void } {
	let answer!: (value: unknown) => void;
	const promise = new Promise((resolve) => {
		answer = resolve;
	});
	return { promise, answer };
}

// Opens the eject confirmation from a focused Eject, as a keyboard user would.
async function openConfirmFrom(modelName: string): Promise<HTMLElement> {
	const eject = screen.getByTestId(`loaded-models-llamacpp-eject-${modelName}`);
	eject.focus();
	fireEvent.click(eject);
	await screen.findByTestId("confirm-cancel");
	return eject;
}

describe("LoadedModelsPage eject confirmation focus", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		runningState.models = [running("alpha"), running("beta")];
		runningState.mutateAsync.mockReset().mockResolvedValue(result("alpha", "ejected"));
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it.each([
		["cancelled", "confirm-cancel"],
		["confirmed", "confirm-accept"],
	])("hands focus to the panel heading when the row went while the dialog was open (%s)", async (_case, closeWith) => {
		const { rerender } = render(page());
		const eject = await openConfirmFrom("alpha");

		runningState.models = [running("beta")];
		rerender(page());
		expect(eject.isConnected).toBe(false);
		fireEvent.click(screen.getByTestId(closeWith));

		const heading = screen.getByRole("heading", { name: "llama.cpp runtime" });
		await waitFor(() => expect(document.activeElement).toBe(heading));
		expect(screen.queryByTestId("confirm-cancel")).toBeNull();
	});

	it("returns focus to the invoking Eject when its row is still there", async () => {
		render(page());
		const eject = await openConfirmFrom("alpha");
		expect(document.activeElement).not.toBe(eject);

		fireEvent.click(screen.getByTestId("confirm-cancel"));

		await waitFor(() => expect(document.activeElement).toBe(eject));
		expect(runningState.mutateAsync).not.toHaveBeenCalled();
	});

	it.each([
		["cancelled", "confirm-cancel"],
		["confirmed", "confirm-accept"],
	])(
		"hands focus to the heading after the force dialog's exit transition when the row went while it was open (%s)",
		async (_case, closeWith) => {
			runningState.mutateAsync
				.mockResolvedValueOnce(result("alpha", "timed_out_still_busy"))
				.mockResolvedValueOnce(result("alpha", "forced"));
			const { rerender } = render(page(true));
			await openConfirmFrom("alpha");
			fireEvent.click(screen.getByTestId("confirm-accept"));
			await screen.findByText("Force eject?");

			runningState.models = [running("beta")];
			rerender(page(true));
			fireEvent.click(screen.getByTestId(closeWith));

			const heading = screen.getByRole("heading", { name: "llama.cpp runtime" });
			await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
			await waitFor(() => expect(document.activeElement).toBe(heading));
			expect(runningState.mutateAsync).toHaveBeenCalledTimes(closeWith === "confirm-accept" ? 2 : 1);
		},
	);

	it("leaves focus on the control the user moved to while the eject request is pending", async () => {
		const request = held();
		runningState.mutateAsync.mockReturnValueOnce(request.promise);
		render(page());
		const eject = await openConfirmFrom("alpha");
		fireEvent.click(screen.getByTestId("confirm-accept"));
		await waitFor(() => expect(document.activeElement).toBe(eject));

		const other = screen.getByTestId("loaded-models-llamacpp-eject-beta");
		other.focus();
		await act(async () => request.answer(result("alpha", "ejected")));

		expect(document.activeElement).toBe(other);
	});

	it("opens no force dialog for an eject a newer one overtook, and keeps the newer confirmation actionable", async () => {
		const first = held();
		runningState.mutateAsync.mockReturnValueOnce(first.promise).mockResolvedValueOnce(result("beta", "ejected"));
		render(page());
		await openConfirmFrom("alpha");
		fireEvent.click(screen.getByTestId("confirm-accept"));
		await waitFor(() => expect(screen.queryByTestId("confirm-accept")).toBeNull());

		await openConfirmFrom("beta");
		await act(async () => first.answer(result("alpha", "timed_out_still_busy")));
		expect(screen.queryByText("Force eject?")).toBeNull();
		expect(screen.getByText("Eject model")).toBeTruthy();
		fireEvent.click(screen.getByTestId("confirm-accept"));

		await waitFor(() => expect(runningState.mutateAsync).toHaveBeenCalledTimes(2));
		expect(runningState.mutateAsync).toHaveBeenLastCalledWith({ modelName: "beta", role: "chat", force: false });
	});

	it("lets the newer eject's dialog decide where focus goes, never the overtaken eject's button", async () => {
		const first = held();
		runningState.mutateAsync.mockReturnValueOnce(first.promise);
		const { rerender } = render(page(true));
		await openConfirmFrom("alpha");
		fireEvent.click(screen.getByTestId("confirm-accept"));
		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());

		await openConfirmFrom("beta");
		await act(async () => first.answer(result("alpha", "timed_out_still_busy")));
		runningState.models = [running("alpha")];
		rerender(page(true));
		fireEvent.click(screen.getByTestId("confirm-cancel"));

		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		await waitFor(() => expect(document.activeElement).toBe(screen.getByRole("heading", { name: "llama.cpp runtime" })));
	});

	it("sends no eject when the first confirmation is accepted after the page has gone", async () => {
		const { rerender } = render(page());
		await openConfirmFrom("alpha");

		rerender(page(false, false));
		expect(screen.queryByTestId("loaded-models-llamacpp-card")).toBeNull();
		await act(async () => fireEvent.click(screen.getByTestId("confirm-accept")));

		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		expect(runningState.mutateAsync).not.toHaveBeenCalled();
	});

	it("sends no force eject when the force dialog is accepted after the page has gone", async () => {
		runningState.mutateAsync.mockResolvedValueOnce(result("alpha", "timed_out_still_busy"));
		const { rerender } = render(page());
		await openConfirmFrom("alpha");
		fireEvent.click(screen.getByTestId("confirm-accept"));
		await screen.findByText("Force eject?");

		rerender(page(false, false));
		expect(screen.queryByTestId("loaded-models-llamacpp-card")).toBeNull();
		await act(async () => fireEvent.click(screen.getByTestId("confirm-accept")));

		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		expect(runningState.mutateAsync).toHaveBeenCalledTimes(1);
	});

	it.each([
		["accepted", "confirm-accept"],
		["cancelled", "confirm-cancel"],
	])("hands focus to the heading once the dialog has closed, before the request settles (%s)", async (_case, closeWith) => {
		const request = held();
		runningState.mutateAsync.mockReturnValueOnce(request.promise);
		const { rerender } = render(page(true));
		await openConfirmFrom("alpha");
		runningState.models = [running("beta")];
		rerender(page(true));

		try {
			fireEvent.click(screen.getByTestId(closeWith));

			await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
			await waitFor(() => expect(document.activeElement).toBe(screen.getByRole("heading", { name: "llama.cpp runtime" })));
			expect(runningState.mutateAsync).toHaveBeenCalledTimes(closeWith === "confirm-accept" ? 1 : 0);
		} finally {
			await act(async () => request.answer(result("alpha", "ejected")));
		}
	});

	it("keeps focus on the body the user moved it to while the request was pending", async () => {
		const request = held();
		runningState.mutateAsync.mockReturnValueOnce(request.promise);
		render(page(true));
		const eject = await openConfirmFrom("alpha");
		fireEvent.click(screen.getByTestId("confirm-accept"));
		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		// The dialog's onClosed runs a frame after it unmounts; a frame queued now runs after it.
		await act(() => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())));
		expect(document.activeElement).toBe(eject);

		// A click on blank page space: the Eject stays mounted.
		eject.blur();
		expect(eject.isConnected).toBe(true);
		await act(async () => request.answer(result("alpha", "ejected")));

		expect(document.activeElement).toBe(document.body);
	});

	it("leaves lost focus alone when an overtaken eject settles", async () => {
		const first = held();
		const second = held();
		runningState.mutateAsync.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
		render(page());
		await openConfirmFrom("alpha");
		fireEvent.click(screen.getByTestId("confirm-accept"));
		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		const beta = await openConfirmFrom("beta");
		fireEvent.click(screen.getByTestId("confirm-accept"));
		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		// Beta's onClosed runs a frame after its dialog unmounts; a frame queued now runs after it, so the blur comes last.
		await act(() => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())));
		expect(document.activeElement).toBe(beta);
		// A click on blank page space while beta's request is still pending.
		beta.blur();

		try {
			await act(async () => first.answer(result("alpha", "ejected")));
			expect(document.activeElement).toBe(document.body);
		} finally {
			await act(async () => second.answer(result("beta", "ejected")));
		}
	});

	// A keepMounted dialog never leaves the DOM, so nothing may wait for it to.
	it("watches nothing when the request settles while another dialog holds focus", async () => {
		const request = held();
		runningState.mutateAsync.mockReturnValueOnce(request.promise);
		render(page());
		await openConfirmFrom("alpha");
		fireEvent.click(screen.getByTestId("confirm-accept"));
		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		render(
			<MantineProvider env="test" theme={testMantineTheme}>
				<Modal opened={true} keepMounted={true} onClose={() => undefined} title="About">
					<button type="button">Close about</button>
				</Modal>
			</MantineProvider>,
		);
		await waitFor(() => expect(document.activeElement?.closest('[role="dialog"]')).not.toBeNull());
		const observe = vi.spyOn(MutationObserver.prototype, "observe");

		try {
			await act(async () => request.answer(result("alpha", "ejected")));
			expect(observe).not.toHaveBeenCalled();
		} finally {
			observe.mockRestore();
		}
	});

	it("opens no force prompt while another confirmation is pending, and that confirmation still gets its answer", async () => {
		const request = held();
		runningState.mutateAsync.mockReturnValueOnce(request.promise);
		const captured: { confirm?: ConfirmContextType["confirm"] } = {};
		render(page(false, true, <CaptureConfirm into={captured} />));
		await openConfirmFrom("alpha");
		fireEvent.click(screen.getByTestId("confirm-accept"));
		await waitFor(() => expect(screen.queryByTestId("confirm-accept")).toBeNull());

		let other: Promise<boolean> | undefined;
		act(() => {
			other = captured.confirm?.({ title: "Other question" });
		});
		await act(async () => request.answer(result("alpha", "timed_out_still_busy")));

		expect(screen.queryByText("Force eject?")).toBeNull();
		expect(screen.getByText("Other question")).toBeTruthy();
		fireEvent.click(screen.getByTestId("confirm-accept"));
		await expect(other).resolves.toBe(true);
	});

	it("opens no force prompt while another dialog holds focus, and leaves focus there", async () => {
		const request = held();
		runningState.mutateAsync.mockReturnValueOnce(request.promise);
		render(page());
		await openConfirmFrom("alpha");
		fireEvent.click(screen.getByTestId("confirm-accept"));
		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		render(
			<MantineProvider env="test" theme={testMantineTheme}>
				<Modal opened={true} onClose={() => undefined} title="About">
					<button type="button">Close about</button>
				</Modal>
			</MantineProvider>,
		);
		const about = await screen.findByRole("dialog", { name: "About" });
		await waitFor(() => expect(about.contains(document.activeElement)).toBe(true));

		await act(async () => request.answer(result("alpha", "timed_out_still_busy")));

		expect(screen.queryByText("Force eject?")).toBeNull();
		expect(about.contains(document.activeElement)).toBe(true);
	});

	it("shows the force prompt for an immediate busy result while the first confirmation still holds focus", async () => {
		runningState.mutateAsync.mockResolvedValueOnce(result("alpha", "timed_out_still_busy"));
		render(page(true));
		await openConfirmFrom("alpha");
		await waitFor(() => expect(document.activeElement?.closest('[role="dialog"]')).not.toBeNull());

		fireEvent.click(screen.getByTestId("confirm-accept"));

		expect(await screen.findByText("Force eject?")).toBeTruthy();
	});

	it.each([
		["the eject confirmation", "confirm-accept", false],
		["the eject confirmation", "confirm-cancel", false],
		["the force confirmation", "confirm-accept", true],
		["the force confirmation", "confirm-cancel", true],
	])("hands focus to the main landmark when %s closes after the page has gone (%s)", async (_case, closeWith, force) => {
		runningState.mutateAsync.mockResolvedValueOnce(result("alpha", "timed_out_still_busy"));
		const { rerender } = render(page(true));
		await openConfirmFrom("alpha");
		if (force) {
			fireEvent.click(screen.getByTestId("confirm-accept"));
			await screen.findByText("Force eject?");
		}

		rerender(page(true, false));
		expect(screen.queryByTestId("loaded-models-llamacpp-card")).toBeNull();
		fireEvent.click(screen.getByTestId(closeWith));

		await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
		await waitFor(() => expect(document.activeElement).toBe(screen.getByRole("main")));
		expect(runningState.mutateAsync).toHaveBeenCalledTimes(force ? 1 : 0);
	});
});
