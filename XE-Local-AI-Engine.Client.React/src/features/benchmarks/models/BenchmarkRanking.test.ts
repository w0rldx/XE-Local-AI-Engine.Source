import { describe, expect, it } from "vitest";

import { benchmarkRankExclusionReasons } from "@/features/benchmarks/models/BenchmarkModels";
import {
	groupBenchmarkRunsByModel,
	hasActiveJudgeAttempt,
	rankExclusionAction,
	sortBenchmarkRuns,
	succeededRunCount,
} from "@/features/benchmarks/models/BenchmarkRanking";
import { benchmarkJudgeFixture, benchmarkRunSummaryFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";

// The table's order is a product decision, not a server one: the node returns dense ranks with ties sharing a rank and
// nulls for everything it excluded, and the UI has to turn that into one stable list where an excluded run is still
// visible and still actionable.

const run = benchmarkRunSummaryFixture;

describe("sortBenchmarkRuns", () => {
	it("orders by rank ascending and pushes every unranked run to the end", () => {
		const ordered = sortBenchmarkRuns([
			run({ id: "unranked", rank: null, createdAtUtc: 10 }),
			run({ id: "second", rank: 2, createdAtUtc: 20 }),
			run({ id: "first", rank: 1, createdAtUtc: 5 }),
		]);

		expect(ordered.map((item) => item.id)).toEqual(["first", "second", "unranked"]);
	});

	// Ties share a rank by contract, so the tie-break has to come from somewhere stable and meaningful.
	it("breaks a shared rank by recency", () => {
		const ordered = sortBenchmarkRuns([
			run({ id: "older", rank: 1, createdAtUtc: 5 }),
			run({ id: "newer", rank: 1, createdAtUtc: 9 }),
		]);

		expect(ordered.map((item) => item.id)).toEqual(["newer", "older"]);
	});

	it("orders the unranked tail newest first", () => {
		const ordered = sortBenchmarkRuns([
			run({ id: "old", rank: null, createdAtUtc: 1 }),
			run({ id: "new", rank: null, createdAtUtc: 7 }),
		]);

		expect(ordered.map((item) => item.id)).toEqual(["new", "old"]);
	});

	it("does not mutate the list it was given", () => {
		const input = [run({ id: "b", rank: 2 }), run({ id: "a", rank: 1 })];

		sortBenchmarkRuns(input);

		expect(input.map((item) => item.id)).toEqual(["b", "a"]);
	});
});

describe("groupBenchmarkRunsByModel", () => {
	// The key comes from the node and is the BASE model now: two quants of one model have different content, so keying
	// on the content fingerprint gave every quant its own group and made "which quant is best" unaskable.
	it("folds a model's quants into one group whose best row is its best quant", () => {
		const groups = groupBenchmarkRunsByModel([
			run({ id: "q4", modelGroupKey: "owner/repo", primaryModelName: "owner/Repo:Q4_K_M", rank: 2, createdAtUtc: 1 }),
			run({ id: "q8", modelGroupKey: "owner/repo", primaryModelName: "owner/Repo:Q8_0", rank: 1, createdAtUtc: 2 }),
			run({ id: "other", modelGroupKey: "owner/other", primaryModelName: "owner/Other:Q4_K_M", rank: 3, createdAtUtc: 3 }),
		]);

		expect(groups.map((group) => group.key)).toEqual(["owner/repo", "owner/other"]);
		expect(groups[0]?.runs.map((item) => item.id)).toEqual(["q8", "q4"]);
		expect(groups[0]?.leader.id).toBe("q8");
	});


	it("collapses a model's runs under its best-ranked one", () => {
		const groups = groupBenchmarkRunsByModel([
			run({ id: "a-old", modelGroupKey: "model-a", rank: 3, createdAtUtc: 1 }),
			run({ id: "a-best", modelGroupKey: "model-a", rank: 1, createdAtUtc: 2 }),
			run({ id: "b", modelGroupKey: "model-b", rank: 2, createdAtUtc: 3 }),
		]);

		expect(groups.map((group) => group.key)).toEqual(["model-a", "model-b"]);
		expect(groups[0]?.leader.id).toBe("a-best");
		expect(groups[0]?.runs.map((item) => item.id)).toEqual(["a-best", "a-old"]);
		expect(groups[1]?.runs).toHaveLength(1);
	});

	// A model whose runs are all excluded still gets a row; it just sorts after every ranked model.
	it("orders a fully unranked model after the ranked ones", () => {
		const groups = groupBenchmarkRunsByModel([
			run({ id: "unranked", modelGroupKey: "model-x", rank: null, createdAtUtc: 9 }),
			run({ id: "ranked", modelGroupKey: "model-y", rank: 1, createdAtUtc: 1 }),
		]);

		expect(groups.map((group) => group.key)).toEqual(["model-y", "model-x"]);
	});
});

describe("rankExclusionAction", () => {
	// Every reason the node can send must map to something the operator can DO; an unmapped reason would render a chip
	// with no next step.
	it.each(benchmarkRankExclusionReasons)("maps %s to an action", (reason) => {
		expect(["score", "rejudge", "wait", "rerun", "none"]).toContain(rankExclusionAction(reason));
	});

	it.each([
		["no-score", "score"],
		["judge-pending", "wait"],
		["judge-failed", "rejudge"],
		["judge-cancelled", "rejudge"],
		["policy-outdated", "rejudge"],
		["generation-stale", "rejudge"],
		["execution-key-mismatch", "rejudge"],
		["execution-identity-incomplete", "rejudge"],
		// Re-judging the same truncated fragment produces the same fragment, so the only useful action is a rerun.
		["truncated", "rerun"],
		// Nothing was answered, so there is nothing for a judge to read either — only another attempt helps.
		["incomplete", "rerun"],
		// The one exclusion that is not a problem: a warm-up is excluded because that is what it is for.
		["warmup", "none"],
	] as const)("maps %s to %s", (reason, action) => {
		expect(rankExclusionAction(reason)).toBe(action);
	});
});

describe("project-level judge guards", () => {
	// The node refuses a project re-judge while any attempt is queued or running, so the button is derived from the
	// same fact rather than from the failed request.
	it("detects an active judging from any run", () => {
		expect(hasActiveJudgeAttempt([run(), run({ judge: benchmarkJudgeFixture({ state: "queued" }) })])).toBe(true);
		expect(hasActiveJudgeAttempt([run({ judge: benchmarkJudgeFixture({ state: "running" }) })])).toBe(true);
		expect(hasActiveJudgeAttempt([run({ judge: benchmarkJudgeFixture({ state: "succeeded", score: 10 }) })])).toBe(false);
		expect(hasActiveJudgeAttempt([])).toBe(false);
	});

	// The confirmation says how many runs a re-judge touches; only succeeded runs have output to judge.
	it("counts only the succeeded runs a re-judge would re-score", () => {
		expect(
			succeededRunCount([run(), run({ primaryStatus: "Failed" }), run({ primaryStatus: "Running" }), run()]),
		).toBe(2);
	});
});
