// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { RunningModel } from "@/core/loaded-models/models/RunningModelsModels";

// The running list is a mutable value the test swaps to stand in for a residency push; the confirmation dialog is the
// real ConfirmProvider, because its focus return is what this file pins.
const { runningState } = vi.hoisted(() => ({
	runningState: { models: [] as RunningModel[], mutate: vi.fn() },
}));

vi.mock("@/core/loaded-models/queries/useRunningModels", () => ({
	useRunningModels: () => ({ data: runningState.models, isLoading: false, error: null }),
	useEjectRunningModel: () => ({ mutate: runningState.mutate, isPending: false, variables: undefined }),
}));

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
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

function page() {
	return (
		<MantineProvider env="test" theme={testMantineTheme}>
			<QueryClientProvider client={queryClient}>
				<ConfirmProvider>
					<LoadedModelsPage />
				</ConfirmProvider>
			</QueryClientProvider>
		</MantineProvider>
	);
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
		expect(runningState.mutate).not.toHaveBeenCalled();
	});
});
