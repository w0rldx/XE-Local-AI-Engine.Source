// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { BenchmarkRunsTable } from "@/features/benchmarks/components/BenchmarkRunsTable";
import type { BenchmarkRankCohort, BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkJudgeFixture, benchmarkRunSummaryFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The ranked table is where the node's ranking becomes readable. Two rules carry it: an unranked run is never hidden
// (it is the row the operator most likely has to act on, so it keeps its reason chip), and the cohort line states what
// the ranking was computed against — "n of m ranked" is meaningless without the policy revision and generation.

const cohort: BenchmarkRankCohort = {
	policyRevision: 2,
	executionKey: "key",
	cohortGeneration: 3,
	rankedCount: 1,
	totalScored: 2,
};

function renderTable(runs: BenchmarkRunSummary[], props: Record<string, unknown> = {}) {
	const handlers = { onToggleRun: vi.fn(), onRejudgeRun: vi.fn(), onDeleteRun: vi.fn() };
	const view = renderWithProviders(
		<BenchmarkRunsTable runs={runs} cohort={cohort} selectedRunIds={[]} {...handlers} {...props} />,
	);
	return { ...view, ...handlers };
}

const rankedRun = benchmarkRunSummaryFixture({
	id: "ranked",
	primaryModelName: "alpha.gguf",
	modelGroupKey: "model-a",
	rank: 1,
	qualityScore: 80,
	qualityScoreSource: "judge",
	judge: benchmarkJudgeFixture({ state: "succeeded", score: 80, policyRevision: 2, policyCurrent: true, executionCurrent: true }),
	createdAtUtc: 2,
});
const excludedRun = benchmarkRunSummaryFixture({
	id: "excluded",
	primaryModelName: "beta.gguf",
	modelGroupKey: "model-b",
	rank: null,
	rankExclusionReason: "policy-outdated",
	qualityScore: 40,
	qualityScoreSource: "judge",
	judge: benchmarkJudgeFixture({ state: "succeeded", score: 40, policyRevision: 1 }),
	createdAtUtc: 1,
});

describe("BenchmarkRunsTable", () => {
	afterEach(cleanup);

	it("states what the ranking was computed against", () => {
		renderTable([rankedRun]);

		const line = screen.getByTestId("benchmark-rank-cohort").textContent ?? "";
		expect(line).toContain("1 of 2 ranked");
		expect(line).toContain("judge policy r2");
		expect(line).toContain("gen 3");
	});

	it("ranks the rows and keeps the unranked one visible with its reason", () => {
		renderTable([excludedRun, rankedRun]);

		const rows = [...screen.getByTestId("benchmark-runs-table").querySelectorAll("tbody tr")];
		expect(rows.map((row) => row.getAttribute("data-testid"))).toEqual([
			"benchmark-run-row-ranked",
			"benchmark-run-row-excluded",
		]);
		expect(screen.getByTestId("benchmark-rank-exclusion-excluded").textContent).toBe("policy outdated");
		expect(screen.queryByTestId("benchmark-rank-exclusion-ranked")).toBeNull();
	});

	// A cancelled judging is not a failed one — the operator only has to re-judge it, and the chip must say so rather
	// than accusing the judge of failing.
	it("distinguishes a cancelled judging from a failed one", () => {
		renderTable([
			benchmarkRunSummaryFixture({ id: "cancelled", rank: null, rankExclusionReason: "judge-cancelled" }),
			benchmarkRunSummaryFixture({ id: "broken", rank: null, rankExclusionReason: "judge-failed" }),
		]);

		expect(screen.getByTestId("benchmark-rank-exclusion-cancelled").textContent).toBe("judge cancelled");
		expect(screen.getByTestId("benchmark-rank-exclusion-broken").textContent).toBe("judge failed");
	});

	// The quality score without its source is ambiguous: an operator override and a judge verdict rank identically but
	// mean different things.
	it("labels where each quality score came from", () => {
		renderTable([
			rankedRun,
			benchmarkRunSummaryFixture({ id: "manual", qualityScore: 90, qualityScoreSource: "user", userScore: 90, rank: 2 }),
		]);

		expect(screen.getByTestId("benchmark-quality-source-ranked").textContent).toBe("judge");
		expect(screen.getByTestId("benchmark-quality-source-manual").textContent).toBe("operator");
	});

	it("reports a row selection to the caller", () => {
		const { onToggleRun } = renderTable([rankedRun]);

		fireEvent.click(screen.getByTestId("benchmark-run-select-ranked"));

		expect(onToggleRun).toHaveBeenCalledExactlyOnceWith("ranked");
	});

	// A truncated run still reads "Succeeded" — it did succeed. The badge next to that status is the only thing that
	// stops the reader from taking a cut-off fragment for a finished answer, and the rank chip says what to do.
	it("badges a truncated run beside its succeeded status and explains why it does not rank", () => {
		const truncated = benchmarkRunSummaryFixture({
			id: "truncated",
			primaryStatus: "Succeeded",
			primaryStopReason: "length",
			rank: null,
			rankExclusionReason: "truncated",
			judge: benchmarkJudgeFixture({ state: "succeeded", score: 96, policyRevision: 2, policyCurrent: true, executionCurrent: true }),
		});
		renderTable([truncated, rankedRun]);

		expect(screen.getByTestId("benchmark-truncated-truncated")).toBeTruthy();
		expect(screen.getByTestId("benchmark-rank-exclusion-truncated").textContent).toContain("truncated");
		// The complete run must NOT be badged, or the signal means nothing.
		expect(screen.queryByTestId("benchmark-truncated-ranked")).toBeNull();
	});

	// Same-model history: collapsed, one row per model; expanded, the model's older runs appear underneath.
	it("groups a model's runs and expands them on request", () => {
		const older = benchmarkRunSummaryFixture({
			id: "older",
			primaryModelName: "alpha.gguf",
			modelGroupKey: "model-a",
			rank: null,
			rankExclusionReason: "generation-stale",
			createdAtUtc: 1,
		});
		renderTable([rankedRun, older, excludedRun]);

		fireEvent.click(screen.getByTestId("benchmark-group-by-model"));

		expect(screen.getByTestId("benchmark-run-row-ranked")).toBeTruthy();
		expect(screen.queryByTestId("benchmark-run-row-older")).toBeNull();
		expect(screen.getByText("2 runs of this model")).toBeTruthy();

		fireEvent.click(screen.getByTestId("benchmark-group-toggle-model-a"));

		expect(screen.getByTestId("benchmark-run-row-older")).toBeTruthy();
	});

	it.each([
		["Re-judge run", "onRejudgeRun"],
		["Delete terminal run", "onDeleteRun"],
	] as const)("offers %s per row", async (label, handler) => {
		const view = renderTable([rankedRun]);

		fireEvent.click(screen.getByTestId("benchmark-run-actions-ranked"));
		fireEvent.click(await screen.findByRole("menuitem", { name: label }));

		expect(view[handler]).toHaveBeenCalledExactlyOnceWith(rankedRun);
	});

	// The node refuses to delete an active run, and only a succeeded primary has output to judge.
	it("refuses a delete while the run is still active", async () => {
		const { onDeleteRun } = renderTable([benchmarkRunSummaryFixture({ id: "live", primaryStatus: "Running" })]);

		fireEvent.click(screen.getByTestId("benchmark-run-actions-live"));
		fireEvent.click(await screen.findByRole("menuitem", { name: "Delete terminal run" }));

		expect(onDeleteRun).not.toHaveBeenCalled();
	});

	it("refuses a re-judge for a run that never succeeded", async () => {
		const { onRejudgeRun } = renderTable([
			benchmarkRunSummaryFixture({ id: "failed", primaryStatus: "Failed", rankExclusionReason: "no-score" }),
		]);

		fireEvent.click(screen.getByTestId("benchmark-run-actions-failed"));
		fireEvent.click(await screen.findByRole("menuitem", { name: "Re-judge run" }));

		expect(onRejudgeRun).not.toHaveBeenCalled();
	});

	it("shows an empty state instead of a bare table head", () => {
		renderTable([]);

		expect(screen.queryByTestId("benchmark-runs-table")).toBeNull();
		expect(screen.getByText("No runs yet. Start one to populate the ranking.")).toBeTruthy();
	});
});
