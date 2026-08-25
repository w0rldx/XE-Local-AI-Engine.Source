import type { BenchmarkRankExclusionReason, BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import { isJudgeActive } from "@/features/benchmarks/models/BenchmarkModels";

/**
 * Ranked first (dense rank, ties share a rank), then everything the node excluded, newest first. The unranked runs are
 * NOT hidden: an excluded run is the one the operator most likely has to act on, and its exclusion reason says how.
 */
export function sortBenchmarkRuns(runs: readonly BenchmarkRunSummary[]): BenchmarkRunSummary[] {
	return [...runs].sort((left, right) => {
		if (left.rank !== right.rank) {
			if (left.rank === null) {
				return 1;
			}
			if (right.rank === null) {
				return -1;
			}
			return left.rank - right.rank;
		}
		return right.createdAtUtc - left.createdAtUtc;
	});
}

export interface BenchmarkModelGroup {
	key: string;
	/** The group's best-ranked run (newest among unranked ones) — the numbers the collapsed row shows. */
	leader: BenchmarkRunSummary;
	/** Every run of that model, in the same order the flat table would show them. */
	runs: BenchmarkRunSummary[];
}

/**
 * One row per BASE model, ordered by its leader. `modelGroupKey` is the base model now — not the content fingerprint,
 * which gave every quant its own group and made "which quant of this model is best" unaskable. So a group is one model,
 * its rows are that model's quants and KV types, and the group's best quant is simply its top-ranked row. Purely
 * client-side over the loaded page — the node already returns every run of the project, so a second request would only
 * re-fetch what is in hand.
 */
export function groupBenchmarkRunsByModel(runs: readonly BenchmarkRunSummary[]): BenchmarkModelGroup[] {
	const groups = new Map<string, BenchmarkRunSummary[]>();
	for (const run of sortBenchmarkRuns(runs)) {
		const existing = groups.get(run.modelGroupKey);
		if (existing) {
			existing.push(run);
		} else {
			groups.set(run.modelGroupKey, [run]);
		}
	}
	return [...groups.entries()]
		.map(([key, grouped]) => ({ key, leader: grouped[0] as BenchmarkRunSummary, runs: grouped }))
		.sort((left, right) => {
			const leftRank = left.leader.rank;
			const rightRank = right.leader.rank;
			if (leftRank !== rightRank) {
				return leftRank === null ? 1 : rightRank === null ? -1 : leftRank - rightRank;
			}
			return right.leader.createdAtUtc - left.leader.createdAtUtc;
		});
}

/**
 * What the operator can do about an exclusion. `wait` = the node is already working on it, `rerun` = re-judging cannot
 * help because the measurement itself is incomplete.
 */
export type BenchmarkRankExclusionAction = "score" | "rejudge" | "wait" | "rerun" | "none";

export function rankExclusionAction(reason: BenchmarkRankExclusionReason): BenchmarkRankExclusionAction {
	switch (reason) {
		case "no-score":
			return "score";
		case "judge-pending":
			return "wait";
		// A truncated answer is not a judging problem: re-judging the same fragment produces the same fragment. The run
		// has to be taken again with more room.
		case "truncated":
			return "rerun";
		// Nothing was answered at all, so there is nothing for a judge to read either — only another attempt helps.
		case "incomplete":
			return "rerun";
		// The only reason that is not a problem: a warm-up is excluded because that is what it is for.
		case "warmup":
			return "none";
		default:
			return "rejudge";
	}
}

/** A project-level re-judge, and every judge activation, is refused by the node while any attempt is still running. */
export const hasActiveJudgeAttempt = (runs: readonly BenchmarkRunSummary[]): boolean =>
	runs.some((run) => isJudgeActive(run.judge.state));

/** How many runs a re-judge of the whole project would actually re-score. */
export const succeededRunCount = (runs: readonly BenchmarkRunSummary[]): number =>
	runs.filter((run) => run.primaryStatus === "Succeeded").length;
