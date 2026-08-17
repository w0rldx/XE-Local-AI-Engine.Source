// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ComparisonCreateDialog } from "@/features/training/components/ComparisonCreateDialog";
import type { EvaluationRun } from "@/features/training/models/ComparisonModels";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, defaultValue?: string) => defaultValue ?? _key }),
}));

const mocks = vi.hoisted(() => ({
	evaluations: [] as EvaluationRun[],
	createEvaluation: vi.fn(),
	createComparison: vi.fn(),
	noop: vi.fn(),
}));

vi.mock("@/features/training/hooks/useTrainingRunHub", () => ({ useTrainingRunHub: mocks.noop }));
vi.mock("@/features/benchmarks/queries/useBenchmarks", () => ({
	useBenchmarkProjects: () => ({ data: [] }),
	useBenchmarkRuns: () => ({ data: { items: [] } }),
}));
vi.mock("@/features/training/queries/useTrainingRuns", () => ({
	useTrainingRuns: () => ({ data: [{ id: "run-1", status: "Succeeded" }] }),
}));
vi.mock("@/features/training/queries/useTrainingComparisons", () => ({
	useComparisonSuggestion: () => ({
		data: {
			trainingRunId: "run-1",
			baseModelName: "base-model",
			tunedModelName: "tuned.gguf",
			baseEvaluationRunId: "old-base",
			tunedEvaluationRunId: "old-tuned",
			unavailableReason: null,
		},
	}),
	useTrainingEvaluations: () => ({ data: mocks.evaluations }),
	useRefreshEvaluations: () => mocks.noop,
	useCreateEvaluation: () => ({ isPending: false, mutate: mocks.createEvaluation }),
	useResumeEvaluation: () => ({ isPending: false, mutate: mocks.noop }),
	useCreateComparison: () => ({ isPending: false, mutate: mocks.createComparison }),
}));

function evaluation(id: string, targetKind: string, sourceArtifactId: string | null, comparisonId: string | null): EvaluationRun {
	return {
		id,
		trainingRunId: "run-1",
		comparisonId,
		modelName: targetKind === "InstalledModel" ? "base-model" : "tuned.gguf",
		targetKind,
		sourceArtifactId,
		datasetId: "dataset-1",
		datasetContentFingerprint: "v1:dataset",
		status: "Succeeded",
		totalCount: 1,
		scoredCount: 1,
		passedCount: 1,
		perKind: [],
		errorMessage: null,
		version: 2,
	};
}

describe("ComparisonCreateDialog revalidation", () => {
	beforeEach(() => {
		mocks.evaluations = [
			evaluation("old-base", "InstalledModel", null, "comparison-old"),
			evaluation("old-tuned", "StagedTrainingArtifact", "artifact-1", "comparison-old"),
		];
		Object.defineProperty(window, "matchMedia", {
			writable: true,
			value: vi.fn().mockImplementation(() => ({ matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() })),
		});
		Object.defineProperty(window, "ResizeObserver", {
			writable: true,
			value: class ResizeObserverMock {
				observe = vi.fn();
				unobserve = vi.fn();
				disconnect = vi.fn();
			},
		});
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("ignores bound evaluations and creates the report only from fresh ids", () => {
		const view = render(
			<MantineProvider>
				<ComparisonCreateDialog
					artifactId="artifact-1"
					freshEvaluations={true}
					initialRunId="run-1"
					onClose={vi.fn()}
					opened={true}
				/>
			</MantineProvider>,
		);

		const evaluateButtons = screen.getAllByRole("button", { name: "Evaluate" });
		expect(evaluateButtons).toHaveLength(2);
		const baseEvaluateButton = evaluateButtons[0];
		if (!baseEvaluateButton) {
			throw new Error("The fresh base evaluation action was not rendered.");
		}
		fireEvent.click(baseEvaluateButton);
		const baseCallbacks = mocks.createEvaluation.mock.calls[0]?.[1] as { onSuccess: (response: { id: string }) => void };
		baseCallbacks.onSuccess({ id: "fresh-base" });
		mocks.evaluations = [...mocks.evaluations, evaluation("fresh-base", "InstalledModel", null, null)];
		view.rerender(
			<MantineProvider>
				<ComparisonCreateDialog artifactId="artifact-1" freshEvaluations={true} initialRunId="run-1" onClose={vi.fn()} opened={true} />
			</MantineProvider>,
		);

		fireEvent.click(screen.getByRole("button", { name: "Evaluate" }));
		const tunedCallbacks = mocks.createEvaluation.mock.calls[1]?.[1] as { onSuccess: (response: { id: string }) => void };
		tunedCallbacks.onSuccess({ id: "fresh-tuned" });
		mocks.evaluations = [...mocks.evaluations, evaluation("fresh-tuned", "StagedTrainingArtifact", "artifact-1", null)];
		view.rerender(
			<MantineProvider>
				<ComparisonCreateDialog artifactId="artifact-1" freshEvaluations={true} initialRunId="run-1" onClose={vi.fn()} opened={true} />
			</MantineProvider>,
		);

		fireEvent.click(screen.getByRole("button", { name: "Create report" }));
		expect(mocks.createComparison).toHaveBeenCalledWith(
			expect.objectContaining({
				body: expect.objectContaining({ baseEvaluationRunId: "fresh-base", tunedEvaluationRunId: "fresh-tuned" }),
			}),
			expect.any(Object),
		);
	});
});
