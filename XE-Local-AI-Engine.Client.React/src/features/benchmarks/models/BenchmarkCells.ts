import type {
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkCellItemResponse as CellItemResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkCellResponse as CellResponse,
} from "@/core/api/generated";
import type { BenchmarkRankExclusionReason } from "@/features/benchmarks/models/BenchmarkModels";
import { toBenchmarkRankExclusionReason } from "@/features/benchmarks/models/BenchmarkModels";
import type { BenchmarkTaskItem } from "@/features/benchmarks/models/BenchmarkTaskItems";

// A CELL is what a suite ranks: one model, one KV type, one repeat group — holding one run per task item. Its score is
// the mean over the items that count, and it ranks only when every one of them produced a rankable score. A
// single-item project has one run per cell, so its numbers are identical to what the runs table has always shown.

/** One item's answer inside a cell. */
export interface BenchmarkCellItem {
	runId: string;
	/** Null on a pre-suite run, which is read as item 0. */
	taskItemId: string | null;
	taskItemIndex: number | null;
	qualityScore: number | null;
	primaryStopReason: string | null;
	rankExclusionReason: BenchmarkRankExclusionReason | null;
}

export interface BenchmarkCell {
	cellKey: string;
	primaryModelName: string;
	modelContentFingerprint: string;
	kvCacheType: string | null;
	repeatGroupId: string | null;
	repeatIndex: number | null;
	/** The mean over the scorable items, or null when the cell is excluded. */
	quality: number | null;
	rank: number | null;
	rankExclusionReason: BenchmarkRankExclusionReason | null;
	items: BenchmarkCellItem[];
}

const toBenchmarkCellItem = (value: CellItemResponse): BenchmarkCellItem => ({
	runId: value.runId ?? "",
	taskItemId: value.taskItemId ?? null,
	taskItemIndex: value.taskItemIndex ?? null,
	qualityScore: value.qualityScore ?? null,
	primaryStopReason: value.primaryStopReason ?? null,
	rankExclusionReason: toBenchmarkRankExclusionReason(value.rankExclusionReason),
});

export const toBenchmarkCell = (value: CellResponse): BenchmarkCell => ({
	cellKey: value.cellKey,
	primaryModelName: value.primaryModelName,
	modelContentFingerprint: value.modelContentFingerprint,
	kvCacheType: value.kvCacheType ?? null,
	repeatGroupId: value.repeatGroupId ?? null,
	repeatIndex: value.repeatIndex ?? null,
	quality: value.quality ?? null,
	rank: value.rank ?? null,
	rankExclusionReason: toBenchmarkRankExclusionReason(value.rankExclusionReason),
	items: (value.items ?? []).map(toBenchmarkCellItem).sort((left, right) => (left.taskItemIndex ?? 0) - (right.taskItemIndex ?? 0)),
});

/** Ranked first, ties by model name so the order is stable across polls; everything excluded goes to the end. */
export function sortBenchmarkCells(cells: readonly BenchmarkCell[]): BenchmarkCell[] {
	return [...cells].sort((left, right) => {
		if (left.rank !== right.rank) {
			return left.rank === null ? 1 : right.rank === null ? -1 : left.rank - right.rank;
		}
		return left.primaryModelName.localeCompare(right.primaryModelName) || left.cellKey.localeCompare(right.cellKey);
	});
}

/**
 * The scorable items this cell never answered. `item-incomplete` says a cell is missing something; this says WHICH,
 * which is the difference between "re-run this cell" and one targeted re-run.
 */
export function missingBenchmarkCellItems(
	cell: BenchmarkCell,
	scorableItems: readonly BenchmarkTaskItem[],
): BenchmarkTaskItem[] {
	const answered = new Set(cell.items.map((item) => item.taskItemId).filter((id): id is string => id !== null));
	// A cell whose runs name no item at all is a pre-suite one and owes nothing: the node ranks it on its own run.
	return answered.size === 0 ? [] : scorableItems.filter((item) => !answered.has(item.id));
}

/** The item rows the cell mean is a mean OF — a NIAH case is reported on its own axis and is deliberately not here. */
export function scorableCellItems(cell: BenchmarkCell, scorableItems: readonly BenchmarkTaskItem[]): BenchmarkCellItem[] {
	const scorable = new Set(scorableItems.map((item) => item.id));
	return cell.items.filter((item) => item.taskItemId === null || scorable.has(item.taskItemId));
}

export interface BenchmarkNiahRecall {
	/** Cases whose answer was graded at all. Ungraded ones are not counted as misses. */
	graded: number;
	found: number;
	/** 0..1, or null when nothing was graded. */
	recall: number | null;
}

/**
 * The long-context axis: how many of a cell's NIAH cases retrieved their needle. Kept OUT of the mean on purpose —
 * recall at 32k and answer quality are different measurements and their average is neither.
 *
 * A case is scored by an exact-match verifier, so its quality is effectively binary; the midpoint split is what turns
 * a weighted rubric's 0/100 into found/not-found without assuming the rubric holds exactly one criterion.
 */
export function benchmarkNiahRecall(cell: BenchmarkCell, items: readonly BenchmarkTaskItem[]): BenchmarkNiahRecall {
	const cases = new Set(items.filter((item) => item.kind === "niahCase").map((item) => item.id));
	const graded = cell.items.filter(
		(item) => item.taskItemId !== null && cases.has(item.taskItemId) && item.qualityScore !== null,
	);
	return {
		graded: graded.length,
		found: graded.filter((item) => (item.qualityScore as number) >= 50).length,
		recall: graded.length === 0 ? null : graded.filter((item) => (item.qualityScore as number) >= 50).length / graded.length,
	};
}
