// @vitest-environment jsdom

import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { TrainingRunLiveProgress, TrainingRunView } from "@/features/training/models/TrainingModels";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

const mocks = vi.hoisted(() => ({
	runs: [] as TrainingRunView[],
	live: {} as TrainingRunLiveProgress,
	hub: vi.fn(),
	query: vi.fn(),
	noop: vi.fn(),
}));

vi.mock("@tanstack/react-router", () => ({
	Link: ({ children, to, ...props }: { children: ReactNode; to: string }) => (
		<a href={to} {...props}>
			{children}
		</a>
	),
}));
vi.mock("@/features/training/queries/useTrainingRuns", () => ({
	useTrainingRuns: (poll?: boolean) => {
		mocks.query(poll);
		return { data: mocks.runs };
	},
	useRefreshTrainingRuns: () => mocks.noop,
	useCancelTrainingRun: () => ({ isPending: false, mutate: mocks.noop }),
}));
vi.mock("@/features/training/queries/useTrainingArtifacts", () => ({ useRefreshTrainingArtifacts: () => mocks.noop }));
vi.mock("@/features/training/hooks/useTrainingRunHub", () => ({
	useTrainingRunHub: (runId: string | null) => {
		mocks.hub(runId);
		return mocks.live;
	},
}));
vi.mock("@/features/training/components/DatasetDriftAlert", () => ({ DatasetDriftAlert: () => null }));
vi.mock("@/features/training/components/TrainingArtifactPanel", () => ({
	TrainingArtifactPanel: ({ onExportStarted }: { onExportStarted: () => void }) => (
		<button type="button" onClick={onExportStarted}>
			Export
		</button>
	),
}));

import { TrainingRunList } from "@/features/training/components/TrainingRunList";

function run(id: string, status: TrainingRunView["status"]): TrainingRunView {
	return {
		id,
		status,
		datasetId: "dataset",
		baseArtifactId: "base",
		datasetRevision: 1,
		datasetContentFingerprint: "frozen",
		errorMessage: null,
		logTail: null,
		progress: null,
		options: null,
		version: 1,
		updatedAtUtc: 1,
	};
}

describe("TrainingRunList", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
		mocks.runs = [];
		mocks.live = { status: null, phase: "training", step: 7, totalSteps: 40, loss: 1.25, message: null };
	});
	afterEach(cleanup);

	it("subscribes to the executing run behind newer queued rows and keeps its counters on that row", () => {
		mocks.runs = [run("newest", "Queued"), run("queued", "Queued"), run("running", "Training")];
		renderWithMantine(<TrainingRunList />);

		expect(mocks.hub).toHaveBeenLastCalledWith("running");
		expect(within(screen.getByTestId("training-run-running")).getByText("step 7 of 40")).toBeTruthy();
		for (const id of ["newest", "queued"]) {
			const row = within(screen.getByTestId(`training-run-${id}`));
			expect(row.queryByText("step 7 of 40")).toBeNull();
			expect(row.queryByText("loss 1.2500")).toBeNull();
			expect(row.queryByRole("progressbar")).toBeNull();
		}
	});

	it("polls queued work without subscribing to a queued row and provides loaded-model guidance", () => {
		mocks.runs = [run("queued", "Queued")];
		renderWithMantine(<TrainingRunList />);

		expect(mocks.hub).toHaveBeenLastCalledWith(null);
		expect(mocks.query).toHaveBeenCalledWith(true);
		expect(screen.getByRole("link", { name: "Loaded models" }).getAttribute("href")).toBe("/loaded-models");
		expect(screen.getByText(/Queued runs wait for other work and loaded models/)).toBeTruthy();
		expect(screen.queryByRole("progressbar")).toBeNull();
	});

	it("lets an export keep its subscription while another run is queued", () => {
		mocks.runs = [run("queued", "Queued"), run("finished", "Succeeded")];
		renderWithMantine(<TrainingRunList />);
		fireEvent.click(screen.getByRole("button", { name: "Export" }));

		expect(mocks.hub).toHaveBeenLastCalledWith("finished");
		expect(within(screen.getByTestId("training-run-queued")).queryByText("step 7 of 40")).toBeNull();
	});
});
