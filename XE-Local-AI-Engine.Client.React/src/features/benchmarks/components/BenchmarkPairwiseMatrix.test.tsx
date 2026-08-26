// @vitest-environment jsdom

import { cleanup, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { comparisonsMock } = vi.hoisted(() => ({ comparisonsMock: vi.fn() }));

vi.mock("@/core/api/generated", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated")>()),
	listBenchmarkComparisons: comparisonsMock,
}));

import { BenchmarkPairwiseMatrix } from "@/features/benchmarks/components/BenchmarkPairwiseMatrix";
import { formatPairwiseScore, groupComparisonsByPair } from "@/features/benchmarks/models/BenchmarkPairwise";
import type { BenchmarkComparison } from "@/features/benchmarks/models/BenchmarkModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

const comparison = (overrides: Partial<BenchmarkComparison> = {}): BenchmarkComparison => ({
	id: "c1",
	runAId: "aaaaaaaa1111",
	runBId: "bbbbbbbb2222",
	order: 0,
	attemptSequence: 1,
	sequence: 1,
	taskCaseId: null,
	status: "Succeeded",
	verdict: "a",
	answerATruncated: false,
	answerBTruncated: false,
	judgeExecutionKey: "key",
	errorMessage: null,
	enqueuedAtUtc: 1,
	completedAtUtc: 2,
	...overrides,
});

const wire = (items: unknown[], fit: unknown = null) => ({
	data: { cohortGeneration: 3, comparisonSetVersion: 2, referenceExecutionKey: "key", items, fit },
});

describe("groupComparisonsByPair", () => {
	// The swap IS the method: the same answers judged both ways, so a position preference cancels rather than becoming
	// a verdict. One row per pair with both orders is what makes a disagreement between them visible.
	it("puts both orders of one pair on one row", () => {
		const rows = groupComparisonsByPair([
			comparison({ id: "c1", order: 0, verdict: "a" }),
			comparison({ id: "c2", order: 1, verdict: "b" }),
		]);

		expect(rows).toHaveLength(1);
		expect(rows[0]?.orders.map((entry) => entry?.verdict)).toEqual(["a", "b"]);
	});

	it("leaves the unjudged order empty rather than collapsing the row", () => {
		const rows = groupComparisonsByPair([comparison({ order: 0 })]);

		expect(rows[0]?.orders[1]).toBeUndefined();
	});

	it("keeps two different pairs apart", () => {
		const rows = groupComparisonsByPair([comparison(), comparison({ id: "c2", runBId: "cccccccc3333" })]);

		expect(rows).toHaveLength(2);
	});
});

describe("formatPairwiseScore", () => {
	const score = (overrides = {}) => ({
		runId: "r1",
		score: 72.42,
		ciLow: 61.0,
		ciHigh: 83.15,
		comparisons: 4,
		bootstrapAppearances: 100,
		reason: null,
		...overrides,
	});

	it("renders the point estimate with its interval", () => {
		expect(formatPairwiseScore(score())).toBe("72.4 (61.0–83.2)");
	});

	it("renders the estimate alone when the node reported no interval", () => {
		expect(formatPairwiseScore(score({ ciLow: null, ciHigh: null }))).toBe("72.4");
	});

	// An unfitted run has no score, and 0 is a real score.
	it("is null with no fitted score", () => {
		expect(formatPairwiseScore(score({ score: null }))).toBeNull();
	});
});

describe("BenchmarkPairwiseMatrix", () => {
	beforeEach(() => vi.clearAllMocks());
	afterEach(cleanup);

	it("says there are no comparisons rather than rendering an empty table", async () => {
		comparisonsMock.mockResolvedValue(wire([]));
		renderWithProviders(<BenchmarkPairwiseMatrix projectId="p1" />);

		expect(await screen.findByText(/No comparisons yet/)).toBeTruthy();
	});

	it("renders both orders of a pair as two verdict cells", async () => {
		comparisonsMock.mockResolvedValue(
			wire([comparison({ id: "c1", order: 0, verdict: "a" }), comparison({ id: "c2", order: 1, verdict: "tie" })]),
		);
		renderWithProviders(<BenchmarkPairwiseMatrix projectId="p1" />);

		expect((await screen.findByTestId("benchmark-pairwise-verdict-c1")).textContent).toBe("aaaaaaaa");
		expect(screen.getByTestId("benchmark-pairwise-verdict-c2").textContent).toBe("tie");
	});

	it("shows the fitted score with its bootstrap interval", async () => {
		comparisonsMock.mockResolvedValue(
			wire([comparison()], {
				fitKey: "fit",
				judgeExecutionKey: "key",
				comparisonSetVersion: 2,
				cohortGeneration: 3,
				iterations: 40,
				bootstrapReplicates: 500,
				isCurrent: true,
				createdAtUtc: 1,
				fittedSetJson: "[]",
				scores: [{ runId: "aaaaaaaa1111", score: 72.4, ciLow: 61, ciHigh: 83.1, comparisons: 4, bootstrapAppearances: 500 }],
			}),
		);
		renderWithProviders(<BenchmarkPairwiseMatrix projectId="p1" />);

		expect((await screen.findByTestId("benchmark-pairwise-score-aaaaaaaa1111")).textContent).toContain("61.0–83.1");
	});

	// A fit that no longer describes the cohort is a score to WITHHOLD, not one to render smaller.
	it("withholds the scores of a fit that is no longer current, and says why", async () => {
		comparisonsMock.mockResolvedValue(
			wire([comparison()], {
				fitKey: "fit",
				judgeExecutionKey: "key",
				comparisonSetVersion: 1,
				cohortGeneration: 2,
				iterations: 40,
				bootstrapReplicates: 500,
				isCurrent: false,
				createdAtUtc: 1,
				fittedSetJson: "[]",
				scores: [{ runId: "aaaaaaaa1111", score: 72.4, ciLow: 61, ciHigh: 83.1, comparisons: 4, bootstrapAppearances: 500 }],
			}),
		);
		renderWithProviders(<BenchmarkPairwiseMatrix projectId="p1" />);

		await waitFor(() => expect(screen.getByTestId("benchmark-pairwise-stale")).toBeTruthy());
		expect(screen.queryByTestId("benchmark-pairwise-scores")).toBeNull();
	});

	it("shows a comparison that has not been judged yet as its status, not as a verdict", async () => {
		comparisonsMock.mockResolvedValue(wire([comparison({ status: "Running", verdict: null })]));
		renderWithProviders(<BenchmarkPairwiseMatrix projectId="p1" />);

		expect((await screen.findByTestId("benchmark-pairwise-status-c1")).textContent).toBe("running");
	});

	// A verdict over a cut-off answer graded a fragment. It still counts in the fit, so the flag is the only thing
	// stopping the reader from taking it for a judgement of the whole answer.
	it("flags a verdict that compared a truncated answer", async () => {
		comparisonsMock.mockResolvedValue(wire([comparison({ answerBTruncated: true })]));
		renderWithProviders(<BenchmarkPairwiseMatrix projectId="p1" />);

		expect((await screen.findByTestId("benchmark-pairwise-truncated-c1")).textContent).toBe("truncated");
	});

	it("does not flag a pair where neither answer was cut off", async () => {
		comparisonsMock.mockResolvedValue(wire([comparison()]));
		renderWithProviders(<BenchmarkPairwiseMatrix projectId="p1" />);

		await screen.findByTestId("benchmark-pairwise-verdict-c1");
		expect(screen.queryByTestId("benchmark-pairwise-truncated-c1")).toBeNull();
	});
});
