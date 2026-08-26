import type {
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkCellItemResponse as CellItemResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkCellResponse as CellResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkPairedDeltaResponse as PairedDeltaResponse,
} from "@/core/api/generated";
import type { BenchmarkRankExclusionReason, BenchmarkRunVerifier } from "@/features/benchmarks/models/BenchmarkModels";
import { toBenchmarkRankExclusionReason } from "@/features/benchmarks/models/BenchmarkModels";
import type { BenchmarkTaskItem } from "@/features/benchmarks/models/BenchmarkTaskItems";
import { niahCaseCriterionId } from "@/features/benchmarks/models/BenchmarkTaskItems";

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
	/** Cases the node reported a verifier verdict for. Only these can be found or missed. */
	graded: number;
	found: number;
	/** Cases with no verdict on record — evidence not loaded, or the criterion never ran. Never counted as misses. */
	unknown: number;
	/** 0..1 over the graded cases, or null when none of them was graded. */
	recall: number | null;
}

/**
 * The long-context axis: how many of a cell's NIAH cases retrieved their needle. Kept OUT of the mean on purpose —
 * recall at 32k and answer quality are different measurements and their average is neither.
 *
 * Read from the CRITERION, never from the case's aggregate score. A needle is decided by one `exact` verifier the
 * generator wrote onto the case, and the aggregate is that criterion's weight mixed with every other criterion in the
 * rubric — so a case can score 60 with the needle missed, or 40 with it found, and any threshold on the aggregate is a
 * guess dressed as a measurement. A case whose verdict is not on record counts as unknown, which is the one honest
 * answer when the evidence is absent.
 */
export function benchmarkNiahRecall(
	cell: BenchmarkCell,
	items: readonly BenchmarkTaskItem[],
	verifiersByRunId: ReadonlyMap<string, readonly BenchmarkRunVerifier[]>,
): BenchmarkNiahRecall {
	const cases = new Map(items.filter((item) => item.kind === "niahCase").map((item) => [item.id, item]));
	let graded = 0;
	let found = 0;
	let unknown = 0;
	for (const answer of cell.items) {
		const item = answer.taskItemId === null ? undefined : cases.get(answer.taskItemId);
		if (item === undefined) {
			continue;
		}
		const criterionId = niahCaseCriterionId(item);
		const verdict =
			criterionId === null ? undefined : verifiersByRunId.get(answer.runId)?.find((verifier) => verifier.id === criterionId);
		if (verdict === undefined) {
			unknown += 1;
			continue;
		}
		graded += 1;
		found += verdict.passed ? 1 : 0;
	}
	return { graded, found, unknown, recall: graded === 0 ? null : found / graded };
}

/**
 * The paired difference between two cells over the items they BOTH answered rankably, with a 95 % percentile
 * bootstrap interval. Paired on purpose: resampling the per-item DIFFERENCES removes item difficulty as a variance
 * source, which is what makes the interval tight enough to separate two models on a suite this small.
 */
export interface BenchmarkPairedDelta {
	aCellKey: string;
	bCellKey: string;
	/** How many items were rankable in both cells; the resampling unit. Never below three — see the absence rule. */
	sharedItemCount: number;
	/** Mean of qualityA − qualityB over the shared items. Positive means A scored higher. */
	delta: number;
	ciLow: number;
	ciHigh: number;
	/** False exactly when 0 lies inside the interval. The node's flag, never re-derived from the two bounds. */
	separated: boolean;
}

export const toBenchmarkPairedDelta = (value: PairedDeltaResponse): BenchmarkPairedDelta => ({
	aCellKey: value.aCellKey,
	bCellKey: value.bCellKey,
	sharedItemCount: value.sharedItemCount ?? 0,
	delta: value.delta ?? 0,
	ciLow: value.ciLow ?? 0,
	ciHigh: value.ciHigh ?? 0,
	separated: value.separated ?? false,
});

/**
 * The delta between two cells in the direction the reader asked for. The node reports one entry per UNORDERED pair,
 * so picking B first has to flip the sign and swap the interval's ends rather than show A − B under a B − A heading.
 *
 * Null means the node reported no entry for the pair, which says "fewer than three shared items" and never "zero
 * difference" — an interval three points cannot support is worse than no interval.
 */
export function benchmarkPairedDeltaFor(
	deltas: readonly BenchmarkPairedDelta[],
	aCellKey: string,
	bCellKey: string,
): BenchmarkPairedDelta | null {
	const direct = deltas.find((entry) => entry.aCellKey === aCellKey && entry.bCellKey === bCellKey);
	if (direct !== undefined) {
		return direct;
	}
	const reversed = deltas.find((entry) => entry.aCellKey === bCellKey && entry.bCellKey === aCellKey);
	return reversed === undefined
		? null
		: { ...reversed, aCellKey, bCellKey, delta: -reversed.delta, ciLow: -reversed.ciHigh, ciHigh: -reversed.ciLow };
}

/** `+6.2` / `−1.4`: the sign is the whole reading, so it is always printed. */
export const formatBenchmarkDelta = (value: number): string => `${value >= 0 ? "+" : "−"}${Math.abs(value).toFixed(1)}`;

/** `owner/Repo Q4_K_M · q8_0` — enough to tell two cells of one project apart in a picker. */
export const benchmarkCellLabel = (cell: BenchmarkCell, autoLabel: string): string => {
	const repeat = cell.repeatIndex === null ? "" : ` #${cell.repeatIndex}`;
	return `${cell.primaryModelName} · ${cell.kvCacheType ?? autoLabel}${repeat}`;
};

/**
 * Whether a paired delta can exist at all. The node reports NO entry below three shared scoring items, so a project
 * with fewer than three of them renders a panel that can never fill however its runs go — honest, and indistinguishable
 * from a bug. Two combinations to compare is the other half.
 */
export const canComparePairedDeltas = (cellCount: number, scorableItemCount: number): boolean =>
	cellCount >= 2 && scorableItemCount >= 3;
