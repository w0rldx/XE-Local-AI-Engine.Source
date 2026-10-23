// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { TrainingRuntimeStatusView } from "@/features/training/models/TrainingModels";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, defaultValue?: string) => defaultValue ?? _key }),
}));

const mocks = vi.hoisted(() => ({
	status: undefined as TrainingRuntimeStatusView | undefined,
	install: vi.fn(),
	remove: vi.fn(),
	confirm: vi.fn(),
}));

vi.mock("@/features/training/queries/useTrainingQueries", () => ({
	useTrainingRuntimeStatus: () => ({ data: mocks.status }),
	useTrainingRuntimePrerequisites: () => ({ data: undefined }),
	useStartTrainingRuntimeInstall: () => ({ isPending: false, mutate: mocks.install }),
	useRemoveTrainingRuntime: () => ({ isPending: false, mutate: mocks.remove }),
}));

// The hub is exercised in its own test; here it only has to report nothing streamed.
vi.mock("@/features/training/hooks/useTrainingRuntimeHub", () => ({
	useTrainingRuntimeHub: () => ({ phase: null, logEntries: [], error: null, reset: vi.fn() }),
}));

vi.mock("@/core/ui/hooks/useConfirm", () => ({ useConfirm: () => ({ confirm: mocks.confirm }) }));

import { TrainingRuntimeCard } from "@/features/training/components/TrainingRuntimeCard";

function status(overrides: Partial<TrainingRuntimeStatusView> = {}): TrainingRuntimeStatusView {
	return {
		phase: "Ready",
		isRunning: false,
		terminal: true,
		logStartSequence: 0,
		logLines: [],
		sanitizedError: null,
		installed: {
			uvVersion: "0.9.0",
			pythonVersion: "3.12.8",
			contractVersion: 1,
			installedAtUtc: 0,
			torchVersion: null,
			unslothVersion: null,
			deviceName: null,
		},
		...overrides,
	};
}

describe("TrainingRuntimeCard", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		mocks.status = status();
		mocks.remove.mockReset();
		mocks.confirm.mockReset();
	});
	afterEach(cleanup);

	it("removes nothing when the operator cancels the confirmation", async () => {
		mocks.confirm.mockResolvedValue(false);
		renderWithMantine(<TrainingRuntimeCard />);

		fireEvent.click(screen.getByRole("button", { name: "Remove runtime" }));

		await waitFor(() => expect(mocks.confirm).toHaveBeenCalledTimes(1));
		await mocks.confirm.mock.results[0]?.value;
		expect(mocks.remove).not.toHaveBeenCalled();
	});

	it("removes the installed runtime once confirmed", async () => {
		mocks.confirm.mockResolvedValue(true);
		renderWithMantine(<TrainingRuntimeCard />);

		fireEvent.click(screen.getByRole("button", { name: "Remove runtime" }));

		await waitFor(() => expect(mocks.remove).toHaveBeenCalledTimes(1));
		expect(String(mocks.confirm.mock.calls[0]?.[0]?.description)).toContain("deleted from disk");
	});

	it("cancels an install in flight without asking", () => {
		mocks.status = status({ phase: "InstallingPackages", isRunning: true, terminal: false, installed: null });
		renderWithMantine(<TrainingRuntimeCard />);

		fireEvent.click(screen.getByRole("button", { name: "Cancel install" }));

		expect(mocks.remove).toHaveBeenCalledTimes(1);
		expect(mocks.confirm).not.toHaveBeenCalled();
	});
});
