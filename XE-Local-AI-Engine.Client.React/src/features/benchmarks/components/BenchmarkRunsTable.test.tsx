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

	// Reasoning exhaustion IS truncation to the node, which excludes it as `truncated`. The badge REPLACES the generic
	// one rather than adding a second: two badges saying "cut off" would not tell the operator which budget to raise.
	it("names the reasoning budget when that is the budget that ran out", () => {
		const exhausted = benchmarkRunSummaryFixture({
			id: "exhausted",
			primaryStatus: "Succeeded",
			primaryStopReason: "reasoning-length",
			rank: null,
			rankExclusionReason: "truncated",
		});
		renderTable([exhausted]);

		expect(screen.getByTestId("benchmark-reasoning-exhausted-exhausted")).toBeTruthy();
		expect(screen.queryByTestId("benchmark-truncated-exhausted")).toBeNull();
	});

	// An answerless run is not a truncated one: no budget ran out, so it gets its own badge and its own reason chip.
	it("badges a run that answered nothing, apart from a truncated one", () => {
		const empty = benchmarkRunSummaryFixture({
			id: "empty",
			primaryStatus: "Succeeded",
			primaryStopReason: "incomplete",
			rank: null,
			rankExclusionReason: "incomplete",
		});
		renderTable([empty]);

		expect(screen.getByTestId("benchmark-incomplete-empty")).toBeTruthy();
		expect(screen.queryByTestId("benchmark-truncated-empty")).toBeNull();
		expect(screen.getByTestId("benchmark-rank-exclusion-empty").textContent).toContain("no answer");
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

	// The regression this pins: quants of ONE model must fold into ONE group header, not one header per quant. The key
	// is the server's base-model modelGroupKey, so two rows that differ only in quant share it — and the quant, which
	// is then the only thing telling the rows apart, has to be visible on each row.
	it("folds a model's quants into one group and names the quant on each row", () => {
		const quant = (id: string, modelName: string, rank: number, createdAtUtc: number) =>
			benchmarkRunSummaryFixture({
				id,
				primaryModelName: modelName,
				modelGroupKey: "unsloth/qwen3.8-27b-gguf",
				rank,
				createdAtUtc,
			});
		renderTable([
			quant("q4", "unsloth/Qwen3.8-27B-GGUF:Q4_K_M", 2, 1),
			quant("q6", "unsloth/Qwen3.8-27B-GGUF:Q6_K", 1, 2),
		]);

		fireEvent.click(screen.getByTestId("benchmark-group-by-model"));

		// One header, and it is the group's BEST quant — which is what "best quant of this model" now means.
		expect(screen.getAllByTestId(/^benchmark-group-toggle-/)).toHaveLength(1);
		expect(screen.getByText("2 runs of this model")).toBeTruthy();
		expect(screen.getByTestId("benchmark-run-quant-q6").textContent).toBe("Q6_K");
		// The header shows the base model, not the leader's full tagged name.
		expect(screen.getByTestId("benchmark-run-row-q6").textContent).toContain("unsloth/Qwen3.8-27B-GGUF");

		fireEvent.click(screen.getByTestId("benchmark-group-toggle-unsloth/qwen3.8-27b-gguf"));

		expect(screen.getByTestId("benchmark-run-quant-q4").textContent).toBe("Q4_K_M");
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

	// tg leads the cell and pp/TTFT ride under it: a model that decodes fast over a slow prefill is a different machine
	// from one that does both fast, and the single blended figure this replaced could not tell the two apart.
	it("shows decode speed with the prompt rate and time to first token under it", () => {
		renderTable([benchmarkRunSummaryFixture({ id: "measured", tokensPerSecond: 24.31 })]);

		const cell = screen.getByTestId("benchmark-throughput-measured");
		expect(cell.textContent).toContain("24.3");
		expect(cell.textContent).toContain("pp 640");
		expect(cell.textContent).toContain("180 ms");
	});

	it("shows dashes, not zeros, for a run whose runtime timed nothing", () => {
		renderTable([
			benchmarkRunSummaryFixture({
				id: "untimed",
				tokensPerSecond: null,
				throughput: {
					ttftMs: null,
					promptTokens: null,
					promptTokensPerSecond: null,
					generationTokens: null,
					generationTokensPerSecond: null,
					cachedPromptTokens: null,
					segmentCount: null,
				},
			}),
		]);

		const cell = screen.getByTestId("benchmark-throughput-untimed");
		expect(cell.textContent).toContain("—");
		expect(cell.textContent).not.toContain("0.0");
	});

	it("shows an empty state instead of a bare table head", () => {
		renderTable([]);

		expect(screen.queryByTestId("benchmark-runs-table")).toBeNull();
		expect(screen.getByText("No runs yet. Start one to populate the ranking.")).toBeTruthy();
	});

	// A batch launch can create more runs than one page holds. Silently ranking a prefix of the project is the bug
	// this line exists to prevent: the table says how much of it is on screen and offers the rest.
	it("says how many of the project's runs are on screen and loads the rest", () => {
		const onLoadMore = vi.fn();
		renderTable([rankedRun, excludedRun], { totalCount: 400, onLoadMore });

		expect(screen.getByTestId("benchmark-runs-loaded").textContent).toBe("Showing 2 of 400 runs");

		fireEvent.click(screen.getByTestId("benchmark-runs-load-more"));

		expect(onLoadMore).toHaveBeenCalledTimes(1);
	});

	it("offers nothing to load once every run is on screen", () => {
		renderTable([rankedRun, excludedRun], { totalCount: 2 });

		expect(screen.queryByTestId("benchmark-runs-loaded")).toBeNull();
		expect(screen.queryByTestId("benchmark-runs-load-more")).toBeNull();
	});
});
