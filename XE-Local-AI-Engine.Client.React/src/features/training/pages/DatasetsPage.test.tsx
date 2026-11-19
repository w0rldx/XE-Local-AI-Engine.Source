// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { listTrainingDatasetsQueryKey, listTrainingDefinitionsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import type { TrainingDefinition } from "@/features/training/models/TrainingDatasetModels";
import type { TrainingDataset } from "@/features/training/models/TrainingModels";
import { acceptConfirm, cancelConfirm, openConfirm } from "@/test/ConfirmFlow";
import { renderWithProviders } from "@/test/RenderWithProviders";

const mocks = vi.hoisted(() => ({
	datasets: [] as TrainingDataset[],
	definitions: [] as TrainingDefinition[],
	noop: vi.fn(),
	deleteDefinition: vi.fn(),
	deleteDataset: vi.fn(),
	exportMutate: vi.fn(),
}));

vi.mock("@/features/training/queries/useTrainingDatasets", () => ({
	useTrainingDefinitions: () => ({ data: mocks.definitions }),
	useTrainingDatasets: () => ({ data: mocks.datasets }),
	useToolMocks: () => ({ data: [] }),
	useGenerateDataset: () => ({ isPending: false, mutate: mocks.noop }),
	useDeleteTrainingDefinition: () => ({ isPending: false, mutate: mocks.deleteDefinition }),
	useDeleteTrainingDataset: () => ({ isPending: false, mutate: mocks.deleteDataset }),
	useCancelTrainingDataset: () => ({ isPending: false, mutate: mocks.noop }),
	useVerifyToolMock: () => ({ isPending: false, mutate: mocks.noop }),
	useExportTrainingDataset: () => ({ isPending: false, mutate: mocks.exportMutate }),
}));

vi.mock("@/features/training/hooks/useDatasetGenerationHub", () => ({
	useDatasetGenerationHub: () => ({ lines: [], sampleCount: 0 }),
}));

vi.mock("@/features/training/components/DatasetSampleReview", () => ({ DatasetSampleReview: () => null }));
vi.mock("@/features/training/components/DefinitionEditorDialog", () => ({ DefinitionEditorDialog: () => null }));

import { DatasetsPage } from "@/features/training/pages/DatasetsPage";

function dataset(): TrainingDataset {
	return {
		id: "dataset-1",
		definitionId: "def-1",
		definitionVersion: 1,
		name: "Tool calls",
		status: "Ready",
		revision: 1,
		contentFingerprint: "abc",
		totalSampleCount: 40,
		goodSampleCount: 30,
		badSampleCount: 10,
		rejectedSampleCount: 0,
		duplicateSampleCount: 0,
		workStatus: null,
		workErrorMessage: null,
		version: 1,
		updatedAtUtc: 1,
	};
}

// The export preview lives in a DialogShell, which is full-screen below 768px. A fixed 400px preview is taller than
// a landscape phone's whole viewport, so the height has to be viewport-relative and a cap rather than a fixed size.
describe("DatasetsPage export preview", () => {
	afterEach(() => {
		cleanup();
		mocks.exportMutate.mockReset();
	});

	it("caps the preview against the viewport instead of pinning it to a fixed height", async () => {
		mocks.datasets = [dataset()];
		mocks.exportMutate.mockImplementation((_variables: unknown, options: { onSuccess: (result: { content: string }) => void }) =>
			options.onSuccess({ content: '{"messages":[]}' }),
		);

		renderWithProviders(
			<ConfirmProvider>
				<DatasetsPage />
			</ConfirmProvider>,
		);
		fireEvent.click(screen.getByText("JSONL"));

		// The dialog opens on the export response, so its content mounts a frame after the click.
		const preview = (await screen.findByTestId("training-export-content")).closest(".mantine-ScrollArea-root") as HTMLElement;
		expect(preview.style.maxHeight).toBe("40vh");
		expect(preview.style.height).toBe("");
	});
});

function definition(): TrainingDefinition {
	return {
		id: "def-1",
		name: "Tool calls",
		teacherModelName: "teacher:Q4",
		teacherOutputMode: "Constrained",
		systemInstructions: "Answer with a tool call.",
		toolNames: ["get_time"],
		sampleKinds: [{ kind: "single-tool-call", count: 12, label: "Good" }],
		holdoutFraction: 0.2,
		temperature: 0.5,
		baseSeed: "42",
		criticEnabled: false,
		criticModelName: null,
		definitionVersion: 3,
		version: 7,
		updatedAtUtc: 1,
	};
}

describe("DatasetsPage deletes", () => {
	afterEach(() => {
		cleanup();
		mocks.datasets = [];
		mocks.definitions = [];
		mocks.deleteDataset.mockReset();
		mocks.deleteDefinition.mockReset();
	});

	it("deletes a dataset with its expected version only once the operator confirms, and nothing on cancel", async () => {
		mocks.datasets = [dataset()];
		renderWithProviders(
			<ConfirmProvider>
				<DatasetsPage />
			</ConfirmProvider>,
		);
		const remove = screen.getByRole("button", { name: "Delete" });

		const dialog = await openConfirm(remove, "Delete this dataset?");
		expect(dialog.textContent).toContain("'Tool calls' and its samples are deleted.");
		await cancelConfirm(remove);
		expect(mocks.deleteDataset).not.toHaveBeenCalled();

		await openConfirm(remove, "Delete this dataset?");
		acceptConfirm();
		await waitFor(() =>
			expect(mocks.deleteDataset).toHaveBeenCalledWith({ datasetId: "dataset-1", expectedVersion: 1 }, expect.any(Object)),
		);
	});

	it("deletes a definition with its expected version only once the operator confirms, and nothing on cancel", async () => {
		mocks.definitions = [definition()];
		renderWithProviders(
			<ConfirmProvider>
				<DatasetsPage />
			</ConfirmProvider>,
		);
		fireEvent.click(screen.getByTestId("training-tab-definitions"));
		const remove = await screen.findByRole("button", { name: "Delete" });

		const dialog = await openConfirm(remove, "Delete this dataset definition?");
		expect(dialog.textContent).toContain("'Tool calls' is deleted.");
		await cancelConfirm(remove);
		expect(mocks.deleteDefinition).not.toHaveBeenCalled();

		await openConfirm(remove, "Delete this dataset definition?");
		acceptConfirm();
		await waitFor(() =>
			expect(mocks.deleteDefinition).toHaveBeenCalledWith({ definitionId: "def-1", expectedVersion: 7 }, expect.any(Object)),
		);
	});

	it("sends the versions the lists hold when the operator confirms, not the ones seen at the click", async () => {
		mocks.datasets = [dataset()];
		mocks.definitions = [definition()];
		const { queryClient } = renderWithProviders(
			<ConfirmProvider>
				<DatasetsPage />
			</ConfirmProvider>,
		);

		// The list polls every 2 s while a dataset generates, so its version can move while the dialog is open.
		await openConfirm(screen.getByRole("button", { name: "Delete" }), "Delete this dataset?");
		queryClient.setQueryData(listTrainingDatasetsQueryKey(), { items: [{ id: "dataset-1", version: 2 }] });
		acceptConfirm();
		await waitFor(() =>
			expect(mocks.deleteDataset).toHaveBeenCalledWith({ datasetId: "dataset-1", expectedVersion: 2 }, expect.any(Object)),
		);

		fireEvent.click(screen.getByTestId("training-tab-definitions"));
		await openConfirm(await screen.findByRole("button", { name: "Delete" }), "Delete this dataset definition?");
		queryClient.setQueryData(listTrainingDefinitionsQueryKey(), [{ ...definition(), version: 8 }]);
		acceptConfirm();
		await waitFor(() =>
			expect(mocks.deleteDefinition).toHaveBeenCalledWith({ definitionId: "def-1", expectedVersion: 8 }, expect.any(Object)),
		);
	});
});
