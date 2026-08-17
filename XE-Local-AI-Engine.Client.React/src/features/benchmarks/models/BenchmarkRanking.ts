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
 * Same-model history: one row per `modelGroupKey`, ordered by its leader. Purely client-side over the loaded page —
 * the node already returns every run of the project, so a second request would only re-fetch what is in hand.
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

/** What the operator can do about an exclusion. `wait` = the node is already working on it, `none` = nothing to do. */
export type BenchmarkRankExclusionAction = "score" | "rejudge" | "wait";

export function rankExclusionAction(reason: BenchmarkRankExclusionReason): BenchmarkRankExclusionAction {
	switch (reason) {
		case "no-score":
			return "score";
		case "judge-pending":
			return "wait";
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
