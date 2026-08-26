// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { ComparisonReport } from "@/features/training/models/ComparisonModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

const mocks = vi.hoisted(() => ({ reports: [] as ComparisonReport[], noop: vi.fn() }));

vi.mock("@/features/training/queries/useTrainingComparisons", () => ({
	useComparisonReports: () => ({ data: mocks.reports }),
	useDeleteComparison: () => ({ isPending: false, mutate: mocks.noop }),
	// No evaluation resolves, so the drift alert stays out of the tree — it owns its own suite.
	useTrainingEvaluations: () => ({ data: [] }),
}));

vi.mock("@/features/training/components/ComparisonCreateDialog", () => ({
	ComparisonCreateDialog: () => null,
}));

import { ComparisonsPage } from "@/features/training/pages/ComparisonsPage";

function report(): ComparisonReport {
	return {
		id: "comparison-1",
		name: "Tool calls, base vs tuned",
		baseEvaluationRunId: "eval-base",
		tunedEvaluationRunId: "eval-tuned",
		baseBenchmarkRunId: "bench-base",
		tunedBenchmarkRunId: "bench-tuned",
		trainingRunId: "run-1",
		version: 1,
		createdAtUtc: 1,
		deltas: {
			baseModelName: "base:Q4",
			tunedModelName: "tuned:Q4",
			baseScoredCount: 40,
			basePassedCount: 22,
			tunedScoredCount: 40,
			tunedPassedCount: 31,
			baseAccuracy: 0.55,
			tunedAccuracy: 0.775,
			accuracyDelta: 0.225,
			perKind: [{ kind: "single-tool-call", baseTotal: 40, basePassed: 22, tunedTotal: 40, tunedPassed: 31, baseAccuracy: 0.55, tunedAccuracy: 0.775, accuracyDelta: 0.225 }],
			accuracyAvailable: true,
			unavailableReason: null,
			benchmark: {
				baseTokensPerSecond: 41.2,
				tunedTokensPerSecond: 39.8,
				tokensPerSecondDelta: -1.4,
				baseDurationMs: 1200,
				tunedDurationMs: 1310,
				baseUserScore: 3,
				tunedUserScore: 4,
				userScoreDelta: 1,
				baseJudgeScore: 71,
				tunedJudgeScore: 84,
				judgeScoreDelta: 13,
			},
		},
	};
}

// Both report tables are four columns of numbers wider than a phone. Each one owns its horizontal scroll, so a
// narrow viewport scrolls the table rather than the whole page.
describe("ComparisonsPage", () => {
	afterEach(cleanup);

	it("keeps both report tables inside their own scroll containers", async () => {
		mocks.reports = [report()];

		renderWithProviders(<ComparisonsPage />, { withRouter: true });

		const tables = await Promise.all(
			["training-comparison-accuracy-table", "training-comparison-benchmark-table"].map((testId) => screen.findByTestId(testId)),
		);

		for (const table of tables) {
			expect(table.closest(".mantine-TableScrollContainer-scrollContainer")).not.toBeNull();
		}
	});
});
