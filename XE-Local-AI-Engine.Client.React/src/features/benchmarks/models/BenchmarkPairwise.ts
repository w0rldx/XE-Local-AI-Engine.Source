import type { BenchmarkComparison, BenchmarkPairwiseRunScore } from "@/features/benchmarks/models/BenchmarkModels";

// Pure reading rules for the pairwise verdicts and the fit they produce. Kept out of the components so the two that
// matter — how a pair's two orders are grouped, and when a score may be rendered at all — are testable without a DOM.

/** `72.4 (61.0–83.1)`. Null below a fitted score: an unfitted run has no number, and 0 is a real score. */
export function formatPairwiseScore(score: BenchmarkPairwiseRunScore): string | null {
	if (score.score === null) {
		return null;
	}
	return score.ciLow === null || score.ciHigh === null
		? score.score.toFixed(1)
		: `${score.score.toFixed(1)} (${score.ciLow.toFixed(1)}–${score.ciHigh.toFixed(1)})`;
}

/** `4 min`, `2 h 10 min`. Seconds below a minute — a pairwise cohort is never that fast, but "0 min" would read wrong. */
export function formatEstimatedDuration(seconds: number): string {
	if (seconds < 60) {
		return `${Math.round(seconds)} s`;
	}
	const minutes = Math.round(seconds / 60);
	return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

/** One pair and both of the orders it was judged in. `orders[0]` is A-first, `orders[1]` is B-first. */
export interface BenchmarkPairRow {
	key: string;
	runAId: string;
	runBId: string;
	/** `undefined` while that order has not been judged — an absent order is not a tie. */
	orders: (BenchmarkComparison | undefined)[];
}

const pairKey = (comparison: BenchmarkComparison): string =>
	`${comparison.runAId}|${comparison.runBId}|${comparison.taskCaseId ?? ""}`;

/**
 * Both orders of one pair on one row. The swap IS the method: the same two answers are judged A-first and B-first so a
 * position preference cancels instead of becoming a verdict. Rendering only one order would hide exactly the bias the
 * second judging exists to remove, and putting them on separate rows would hide a disagreement between them.
 */
export function groupComparisonsByPair(comparisons: readonly BenchmarkComparison[]): BenchmarkPairRow[] {
	const rows = new Map<string, BenchmarkPairRow>();
	for (const comparison of comparisons) {
		const key = pairKey(comparison);
		const row = rows.get(key) ?? { key, runAId: comparison.runAId, runBId: comparison.runBId, orders: [undefined, undefined] };
		row.orders[comparison.order === 1 ? 1 : 0] = comparison;
		rows.set(key, row);
	}
	return [...rows.values()];
}
