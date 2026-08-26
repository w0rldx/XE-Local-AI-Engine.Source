import { describe, expect, it } from "vitest";

import {
	benchmarkNiahRecall,
	canComparePairedDeltas,
	missingBenchmarkCellItems,
	scorableCellItems,
	sortBenchmarkCells,
	toBenchmarkCell,
} from "@/features/benchmarks/models/BenchmarkCells";
import { benchmarkCellFixture, benchmarkTaskItemFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";

describe("toBenchmarkCell", () => {
	it("orders a cell's answers by their item index, whatever order the node listed them in", () => {
		const cell = toBenchmarkCell({
			cellKey: "cell:g:1",
			primaryModelName: "m",
			modelContentFingerprint: "v1:m",
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
				rankExclusionReason: "item-incomplete",
			}).rankExclusionReason,
		).toBe("item-incomplete");
		expect(
			toBenchmarkCell({ cellKey: "c", primaryModelName: "m", modelContentFingerprint: "f", rankExclusionReason: "brand-new" })
				.rankExclusionReason,
		).toBeNull();
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
				{
					runId: "r1",
					taskItemId: "a",
					taskItemIndex: 0,
					qualityScore: 80,
					primaryStopReason: "stop",
					rankExclusionReason: null,
				},
			],
		});

		expect(missingBenchmarkCellItems(cell, items).map((item) => item.id)).toEqual(["b", "c"]);
	});

	// A pre-suite run names no item and the node ranks it on its own run; asking it for the current item set would
	// unrank a whole project's history the moment the legacy backfill materialises item 0.
	it("asks nothing of a cell whose runs name no item at all", () => {
		const legacy = benchmarkCellFixture({
			items: [
				{
					runId: "r1",
					taskItemId: null,
					taskItemIndex: null,
					qualityScore: 80,
					primaryStopReason: "stop",
					rankExclusionReason: null,
				},
			],
		});

		expect(missingBenchmarkCellItems(legacy, items)).toEqual([]);
	});
});

describe("scorableCellItems", () => {
	it("leaves out the answers to items that do not count toward the mean", () => {
		const cell = benchmarkCellFixture({
			items: [
				{
					runId: "r1",
					taskItemId: "a",
					taskItemIndex: 0,
					qualityScore: 80,
					primaryStopReason: "stop",
					rankExclusionReason: null,
				},
				{
					runId: "r2",
					taskItemId: "case",
					taskItemIndex: 1,
					qualityScore: 100,
					primaryStopReason: "stop",
					rankExclusionReason: null,
				},
			],
		});

		expect(scorableCellItems(cell, [benchmarkTaskItemFixture({ id: "a" })]).map((item) => item.runId)).toEqual(["r1"]);
	});
});

describe("benchmarkNiahRecall", () => {
	const niahCase = (id: string) =>
		benchmarkTaskItemFixture({ id, kind: "niahCase", countsTowardScore: false, verifierConfig: { recall: { expected: "x" } } });
	const cases = [niahCase("n1"), niahCase("n2"), niahCase("n3")];
	const answer = (runId: string, taskItemId: string, taskItemIndex: number, qualityScore: number | null) => ({
		runId,
		taskItemId,
		taskItemIndex,
		qualityScore,
		primaryStopReason: "stop",
		rankExclusionReason: null,
	});
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
