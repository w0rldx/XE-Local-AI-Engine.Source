import { describe, expect, it } from "vitest";

import {
	benchmarkCellLabel,
	benchmarkNiahRecall,
	benchmarkReasoningReadout,
	canComparePairedDeltas,
	hasBenchmarkAgentVariants,
	hasBenchmarkBudgetVariants,
	missingBenchmarkCellItems,
	sortBenchmarkCells,
	toBenchmarkCell,
} from "@/features/benchmarks/models/BenchmarkCells";
import {
	benchmarkCellFixture,
	benchmarkCellItemFixture,
	benchmarkTaskItemFixture,
} from "@/features/benchmarks/test/BenchmarkTestFixtures";

describe("benchmarkReasoningReadout", () => {
	// "at budget m/n" names its own denominator: an answer whose flag is unknown (multi-round, nothing measured) is
	// left out of n, never counted as "below budget".
	it("counts at-budget answers over the answers where the flag is known", () => {
		const readout = benchmarkReasoningReadout([
			benchmarkCellItemFixture({
				reasoningTokens: 1023,
				nonReasoningOutputTokens: 77,
				durationMs: 4000,
				reasoningAtBudget: true,
			}),
			benchmarkCellItemFixture({
				reasoningTokens: 401,
				nonReasoningOutputTokens: 99,
				durationMs: 2000,
				reasoningAtBudget: false,
			}),
			// A tool round: the node withholds both derived readings, so this answer counts in neither mean nor denominator.
			benchmarkCellItemFixture({
				reasoningTokens: 700,
				nonReasoningOutputTokens: null,
				durationMs: 3000,
				reasoningAtBudget: null,
			}),
		]);

		expect(readout).toEqual({
			meanReasoningTokens: 708,
			meanNonReasoningTokens: 88,
			medianDurationMs: 3000,
			atBudget: 1,
			atBudgetMeasured: 2,
		});
	});

	it("reports nothing rather than zeros when no answer measured reasoning", () => {
		expect(benchmarkReasoningReadout([benchmarkCellItemFixture()])).toEqual({
			meanReasoningTokens: null,
			meanNonReasoningTokens: null,
			medianDurationMs: null,
			atBudget: 0,
			atBudgetMeasured: 0,
		});
	});
});

describe("budget variants", () => {
	// One model at two budgets is two cells; their picker labels must differ, and a one-budget project reads as before.
	it("names the budget only when the cells froze more than one", () => {
		const low = benchmarkCellFixture({ cellKey: "a", primaryModelName: "m", kvCacheType: "f16", reasoningBudgetTokens: 512 });
		const high = benchmarkCellFixture({ cellKey: "b", primaryModelName: "m", kvCacheType: "f16", reasoningBudgetTokens: 4096 });

		expect(hasBenchmarkBudgetVariants([low, { ...low, cellKey: "c" }])).toBe(false);
		expect(hasBenchmarkBudgetVariants([low, high])).toBe(true);
		expect(benchmarkCellLabel(low, "Auto", false, "512 thinking tokens")).toBe("m · f16 · 512 thinking tokens");
	});

	it("maps the cell's budget and each answer's reasoning measurements from the node", () => {
		const cell = toBenchmarkCell({
			cellKey: "c",
			primaryModelName: "m",
			modelContentFingerprint: "f",
			agentName: "a",
			reasoningBudgetTokens: 2048,
			items: [
				{ runId: "r", reasoningTokens: 2047, nonReasoningOutputTokens: 53, durationMs: 900, reasoningAtBudget: true },
				{ runId: "s", reasoningTokens: 300 },
			],
		});

		expect(cell.reasoningBudgetTokens).toBe(2048);
		expect(cell.items[0]).toMatchObject({
			reasoningTokens: 2047,
			nonReasoningOutputTokens: 53,
			durationMs: 900,
			reasoningAtBudget: true,
		});
		expect(cell.items[1]).toMatchObject({ reasoningTokens: 300, nonReasoningOutputTokens: null, reasoningAtBudget: null });
	});
});

describe("toBenchmarkCell", () => {
	it("orders a cell's answers by their item index, whatever order the node listed them in", () => {
		const cell = toBenchmarkCell({
			cellKey: "cell:g:1",
			primaryModelName: "m",
			modelContentFingerprint: "v1:m",
			agentName: "a",
			items: [
				{ runId: "r2", taskItemId: "b", taskItemIndex: 2 },
				{ runId: "r0", taskItemId: "a", taskItemIndex: 0 },
			],
		});

		expect(cell.items.map((item) => item.runId)).toEqual(["r0", "r2"]);
	});

	// An unrecognized reason is dropped rather than rendered raw, the same as everywhere else in this feature.
	it("keeps a known exclusion reason and drops an unknown one", () => {
		expect(
			toBenchmarkCell({
				cellKey: "c",
				primaryModelName: "m",
				modelContentFingerprint: "f",
				agentName: "a",
				rankExclusionReason: "item-incomplete",
			}).rankExclusionReason,
		).toBe("item-incomplete");
		expect(
			toBenchmarkCell({
				cellKey: "c",
				primaryModelName: "m",
				modelContentFingerprint: "f",
				agentName: "a",
				rankExclusionReason: "brand-new",
			}).rankExclusionReason,
		).toBeNull();
	});

	// The agent is part of what a cell measured once a project launches variants, so the mapper must carry all three
	// facts; a missing version is unknown, never version 0.
	it("carries the cell's agent definition, name and version", () => {
		const cell = toBenchmarkCell({
			cellKey: "c",
			primaryModelName: "m",
			modelContentFingerprint: "f",
			agentDefinitionId: "agent-b",
			agentName: "Summariser",
			agentVersion: 3,
		});

		expect(cell).toMatchObject({ agentDefinitionId: "agent-b", agentName: "Summariser", agentVersion: 3 });
		expect(
			toBenchmarkCell({ cellKey: "c", primaryModelName: "m", modelContentFingerprint: "f", agentName: "a" }).agentVersion,
		).toBeNull();
	});
});

describe("benchmarkCellLabel", () => {
	// Two variants of one model at one KV type would otherwise read as the same entry in the paired-delta picker.
	it("names the agent only when asked to", () => {
		const cell = benchmarkCellFixture({ primaryModelName: "m", kvCacheType: "f16", agentName: "Summariser", agentVersion: 2 });

		expect(benchmarkCellLabel(cell, "Auto")).toBe("m · f16");
		expect(benchmarkCellLabel(cell, "Auto", true)).toBe("m · f16 · Summariser v2");
	});

	it("counts variants by agent definition, not by name", () => {
		const a = benchmarkCellFixture({ cellKey: "a", agentDefinitionId: "agent-1", agentName: "Same" });
		const b = benchmarkCellFixture({ cellKey: "b", agentDefinitionId: "agent-2", agentName: "Same" });

		expect(hasBenchmarkAgentVariants([a, benchmarkCellFixture({ cellKey: "c" })])).toBe(false);
		expect(hasBenchmarkAgentVariants([a, b])).toBe(true);
	});
});

describe("sortBenchmarkCells", () => {
	it("ranks first and pushes every excluded cell to the end", () => {
		const ordered = sortBenchmarkCells([
			benchmarkCellFixture({ cellKey: "excluded", rank: null, rankExclusionReason: "item-incomplete" }),
			benchmarkCellFixture({ cellKey: "second", rank: 2 }),
			benchmarkCellFixture({ cellKey: "first", rank: 1 }),
		]);

		expect(ordered.map((cell) => cell.cellKey)).toEqual(["first", "second", "excluded"]);
	});

	// Ties share a rank by contract, so the tie-break has to be stable across a two-second poll.
	it("breaks a shared rank on the model name and then the key", () => {
		const ordered = sortBenchmarkCells([
			benchmarkCellFixture({ cellKey: "b", rank: 1, primaryModelName: "zeta" }),
			benchmarkCellFixture({ cellKey: "a", rank: 1, primaryModelName: "alpha" }),
		]);

		expect(ordered.map((cell) => cell.cellKey)).toEqual(["a", "b"]);
	});
});

describe("missingBenchmarkCellItems", () => {
	const items = [
		benchmarkTaskItemFixture({ id: "a", index: 0 }),
		benchmarkTaskItemFixture({ id: "b", index: 1 }),
		benchmarkTaskItemFixture({ id: "c", index: 2 }),
	];

	// `item-incomplete` says a cell is missing something; this says WHICH, which is the difference between re-running
	// a whole cell and re-running one item of it.
	it("names the scorable items the cell never answered", () => {
		const cell = benchmarkCellFixture({
			items: [
				benchmarkCellItemFixture({
					runId: "r1",
					taskItemId: "a",
					taskItemIndex: 0,
					qualityScore: 80,
					primaryStopReason: "stop",
					rankExclusionReason: null,
				}),
			],
		});

		expect(missingBenchmarkCellItems(cell, items).map((item) => item.id)).toEqual(["b", "c"]);
	});

	// A pre-suite run names no item and the node ranks it on its own run; asking it for the current item set would
	// unrank a whole project's history the moment the legacy backfill materializes item 0.
	it("asks nothing of a cell whose runs name no item at all", () => {
		const legacy = benchmarkCellFixture({
			items: [
				benchmarkCellItemFixture({
					runId: "r1",
					taskItemId: null,
					taskItemIndex: null,
					qualityScore: 80,
					primaryStopReason: "stop",
					rankExclusionReason: null,
				}),
			],
		});

		expect(missingBenchmarkCellItems(legacy, items)).toEqual([]);
	});
});

describe("benchmarkNiahRecall", () => {
	const niahCase = (id: string) =>
		benchmarkTaskItemFixture({ id, kind: "niahCase", countsTowardScore: false, verifierConfig: { recall: { expected: "x" } } });
	const cases = [niahCase("n1"), niahCase("n2"), niahCase("n3")];
	const answer = (runId: string, taskItemId: string, taskItemIndex: number, qualityScore: number | null) =>
		benchmarkCellItemFixture({ runId, taskItemId, taskItemIndex, qualityScore });
	const verdict = (passed: boolean) => [{ id: "recall", kind: "exact", passed, detail: "" }];

	it("reports found over graded from each case's own recall criterion", () => {
		const cell = benchmarkCellFixture({
			items: [answer("r1", "n1", 0, 100), answer("r2", "n2", 1, 0)],
		});

		const recall = benchmarkNiahRecall(
			cell,
			cases,
			new Map([
				["r1", verdict(true)],
				["r2", verdict(false)],
			]),
		);

		expect(recall).toEqual({ graded: 2, found: 1, unknown: 0, recall: 0.5 });
	});

	// The whole finding: with more than one criterion in the rubric the aggregate carries the OTHER criteria's weight,
	// so a case can pass the midpoint with the needle missed. The criterion decides; the aggregate never does.
	it("calls a needle missed when its criterion failed, however high the weighted aggregate came out", () => {
		const cell = benchmarkCellFixture({ items: [answer("r1", "n1", 0, 72)] });

		expect(benchmarkNiahRecall(cell, cases, new Map([["r1", verdict(false)]]))).toEqual({
			graded: 1,
			found: 0,
			unknown: 0,
			recall: 0,
		});
	});

	// And the mirror: a case that missed the midpoint on style criteria still FOUND its needle.
	it("calls a needle found when its criterion passed, however low the weighted aggregate came out", () => {
		const cell = benchmarkCellFixture({ items: [answer("r1", "n1", 0, 31)] });

		expect(benchmarkNiahRecall(cell, cases, new Map([["r1", verdict(true)]])).found).toBe(1);
	});

	// Absent evidence is not a miss. A run whose detail is not loaded, and a case whose criterion never ran, are both
	// "we do not know" — reporting either as a missed needle invents a failure the suite never measured.
	it("counts a case with no verdict on record as unknown rather than as a miss", () => {
		const cell = benchmarkCellFixture({ items: [answer("r1", "n1", 0, 100), answer("r2", "n2", 1, 100)] });

		expect(benchmarkNiahRecall(cell, cases, new Map([["r1", verdict(true)]]))).toEqual({
			graded: 1,
			found: 1,
			unknown: 1,
			recall: 1,
		});
		expect(benchmarkNiahRecall(cell, cases, new Map())).toEqual({ graded: 0, found: 0, unknown: 2, recall: null });
	});

	// Evidence for a DIFFERENT criterion of the same run says nothing about the needle.
	it("does not read another criterion's verdict as the needle's", () => {
		const cell = benchmarkCellFixture({ items: [answer("r1", "n1", 0, 100)] });

		expect(
			benchmarkNiahRecall(cell, cases, new Map([["r1", [{ id: "accuracy", kind: "exact", passed: true, detail: "" }]]])),
		).toEqual({ graded: 0, found: 0, unknown: 1, recall: null });
	});

	it("has no recall to report when the project has no cases", () => {
		expect(benchmarkNiahRecall(benchmarkCellFixture(), [benchmarkTaskItemFixture({ id: "item-1" })], new Map())).toEqual({
			graded: 0,
			found: 0,
			unknown: 0,
			recall: null,
		});
	});
});

describe("canComparePairedDeltas", () => {
	// A two-item suite maxes out at two shared items, which is below the node's minimum — so the panel would render
	// "too few shared items" forever, no matter how the runs go.
	it("refuses a project that can never produce a delta", () => {
		expect(canComparePairedDeltas(2, 2)).toBe(false);
		expect(canComparePairedDeltas(1, 5)).toBe(false);
		expect(canComparePairedDeltas(2, 3)).toBe(true);
	});
});
