import { describe, expect, it } from "vitest";

import { benchmarkReviewBlocker, benchmarkScoreControl } from "@/features/benchmarks/models/BenchmarkReviewModels";

const review = { requiresReview: true };
const run = (overrides: { isWarmup?: boolean; taskItemId?: string | null } = {}) => ({
	isWarmup: false,
	taskItemId: "scored" as string | null,
	...overrides,
});
const items = [
	{ id: "scored", isLeaf: true, countsTowardScore: true },
	{ id: "niah", isLeaf: true, countsTowardScore: false },
];

// The pane must never offer a control the node refuses: in a review project a plain score is a 409 on EVERY run, and a
// review is refused on every run outside the gate.
describe("benchmarkScoreControl", () => {
	it("offers the plain override outside a review project", () => {
		expect(benchmarkScoreControl({ requiresReview: false }, run(), undefined)).toBe("picker");
	});

	it("offers a review on a scoring item and on a pre-suite run with no item", () => {
		expect(benchmarkScoreControl(review, run(), items)).toBe("review");
		expect(benchmarkScoreControl(review, run({ taskItemId: null }), undefined)).toBe("review");
	});

	it.each([
		["a warm-up", run({ isWarmup: true })],
		["a non-scoring leaf", run({ taskItemId: "niah" })],
		["an item missing from the project", run({ taskItemId: "gone" })],
	])("offers no control on %s of a review project", (_, value) => {
		expect(benchmarkScoreControl(review, value, items)).toBe("judgeOnly");
	});

	// Rendering a guess while the reads are in flight would flash the wrong control.
	it("decides nothing until the project and the run's item are known", () => {
		expect(benchmarkScoreControl(undefined, run(), items)).toBe("loading");
		expect(benchmarkScoreControl(review, run(), undefined)).toBe("loading");
	});

	describe("benchmarkReviewBlocker", () => {
		const reviewable = {
			primaryStatus: "Succeeded",
			rankExclusionReason: "review-pending",
			judge: { state: "succeeded", policyCurrent: true },
		};

		it("lets a succeeded, unrevised run under a current judging be reviewed", () => {
			expect(benchmarkReviewBlocker(reviewable, true)).toBeNull();
		});

		it("blocks a run whose primary did not succeed, whatever the judging says", () => {
			expect(benchmarkReviewBlocker({ ...reviewable, primaryStatus: "Failed" }, true)).toBe("primary");
			expect(benchmarkReviewBlocker({ ...reviewable, primaryStatus: "Running" }, false)).toBe("primary");
		});

		// An answer to an edited question never ranks again, so no review of it can count.
		it.each(["item-revised", "item-set-revised"])("blocks an answer excluded as %s", (reason) => {
			expect(benchmarkReviewBlocker({ ...reviewable, rankExclusionReason: reason }, false)).toBe("revised");
		});

		it("needs no judging at all on a project without a judge policy", () => {
			expect(benchmarkReviewBlocker({ ...reviewable, judge: { state: "none", policyCurrent: false } }, false)).toBeNull();
		});
	});
});
