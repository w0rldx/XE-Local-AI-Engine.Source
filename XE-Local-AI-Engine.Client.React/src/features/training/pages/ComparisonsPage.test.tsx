// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { ComparisonReport } from "@/features/training/models/ComparisonModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

const { comparisonsMock } = vi.hoisted(() => ({
	comparisonsMock: {
		useComparisonReports: vi.fn(),
		useDeleteComparison: vi.fn(),
		useTrainingEvaluations: vi.fn(),
	},
}));

vi.mock("@/features/training/queries/useTrainingComparisons", () => comparisonsMock);
vi.mock("@/features/training/components/ComparisonCreateDialog", () => ({
	ComparisonCreateDialog: () => null,
}));

import { ComparisonsPage } from "@/features/training/pages/ComparisonsPage";

const report: ComparisonReport = {
	id: "comparison-1",
	name: "Base versus tuned",
	baseEvaluationRunId: "evaluation-base",
	tunedEvaluationRunId: "evaluation-tuned",
	baseBenchmarkRunId: "benchmark-base",
	tunedBenchmarkRunId: "benchmark-tuned",
	trainingRunId: "training-run-1",
	version: 7,
	createdAtUtc: 1,
	deltas: {
		baseModelName: "base.gguf",
		tunedModelName: "tuned.gguf",
		baseScoredCount: 20,
		basePassedCount: 15,
		tunedScoredCount: 20,
		tunedPassedCount: 18,
		baseAccuracy: 0.75,
		tunedAccuracy: 0.9,
		accuracyDelta: 0.15,
		accuracyAvailable: true,
		unavailableReason: null,
		perKind: [
			{
				kind: "instruction",
				baseTotal: 10,
				basePassed: 7,
				tunedTotal: 10,
				tunedPassed: 9,
				baseAccuracy: 0.7,
				tunedAccuracy: 0.9,
				accuracyDelta: 0.2,
			},
		],
		benchmark: {
			baseTokensPerSecond: 12,
			tunedTokensPerSecond: 14.25,
			tokensPerSecondDelta: 2.25,
			baseDurationMs: 800,
			tunedDurationMs: 700,
			baseUserScore: null,
			tunedUserScore: null,
			userScoreDelta: null,
			baseJudgeScore: 3.5,
			tunedJudgeScore: 4,
			judgeScoreDelta: 0.5,
		},
	},
};

describe("ComparisonsPage", () => {
	const deleteMutate = vi.fn();

	afterEach(cleanup);

	beforeEach(() => {
		vi.clearAllMocks();
		comparisonsMock.useComparisonReports.mockReturnValue({ data: [report] });
		comparisonsMock.useDeleteComparison.mockReturnValue({ mutate: deleteMutate });
		comparisonsMock.useTrainingEvaluations.mockReturnValue({ data: [] });
	});

	it("renders comparison accuracy and benchmark values from a report", async () => {
		renderWithProviders(<ComparisonsPage />, { withRouter: true });

		expect(await screen.findByRole("heading", { name: "Base versus tuned" })).toBeTruthy();
		expect(screen.getByText("instruction")).toBeTruthy();
		expect(screen.getByText("+20.0pp")).toBeTruthy();
		expect(screen.getByText("14.25")).toBeTruthy();
		expect(screen.getByText("+2.25")).toBeTruthy();
		expect(screen.getByText("Compare outputs live on the benchmarks page")).toBeTruthy();
	});

	it("keeps both report tables inside their own scroll containers", async () => {
		renderWithProviders(<ComparisonsPage />, { withRouter: true });
		const tables = await Promise.all(
			["training-comparison-accuracy-table", "training-comparison-benchmark-table"].map((testId) => screen.findByTestId(testId)),
		);
		for (const table of tables) {
			expect(table.closest(".mantine-TableScrollContainer-scrollContainer")).not.toBeNull();
		}
	});

	it("deletes a report with its identifier and expected version", async () => {
		renderWithProviders(<ComparisonsPage />, { withRouter: true });

		fireEvent.click(await screen.findByRole("button", { name: "Delete" }));

		expect(deleteMutate).toHaveBeenCalledOnce();
		expect(deleteMutate).toHaveBeenCalledWith(
			{ path: { comparisonId: "comparison-1" }, body: { expectedVersion: 7 } },
			expect.objectContaining({ onError: expect.any(Function) }),
		);
	});
});
