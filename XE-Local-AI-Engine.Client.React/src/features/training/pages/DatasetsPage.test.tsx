// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { TrainingDataset } from "@/features/training/models/TrainingModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

const mocks = vi.hoisted(() => ({
	datasets: [] as TrainingDataset[],
	noop: vi.fn(),
	exportMutate: vi.fn(),
}));

vi.mock("@/features/training/queries/useTrainingDatasets", () => ({
	useTrainingDefinitions: () => ({ data: [] }),
	useTrainingDatasets: () => ({ data: mocks.datasets }),
	useToolMocks: () => ({ data: [] }),
	useGenerateDataset: () => ({ isPending: false, mutate: mocks.noop }),
	useDeleteTrainingDefinition: () => ({ isPending: false, mutate: mocks.noop }),
	useDeleteTrainingDataset: () => ({ isPending: false, mutate: mocks.noop }),
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
		mocks.exportMutate.mockImplementation(
			(_variables: unknown, options: { onSuccess: (result: { content: string }) => void }) =>
				options.onSuccess({ content: '{"messages":[]}' }),
		);

		renderWithProviders(<DatasetsPage />);
		fireEvent.click(screen.getByText("JSONL"));

		// The dialog opens on the export response, so its content mounts a frame after the click.
		const preview = (await screen.findByTestId("training-export-content")).closest(".mantine-ScrollArea-root") as HTMLElement;
		expect(preview.style.maxHeight).toBe("40vh");
		expect(preview.style.height).toBe("");
	});
});
