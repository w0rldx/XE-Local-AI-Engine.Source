import type { BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkTaskItemLimits } from "@/features/benchmarks/models/BenchmarkTaskItems";

// What a launch is about to cost, before the operator commits to it. A suite multiplies: three items x four cells x
// two repeats is twenty-four model loads, and the arithmetic is not obvious from a dialog that only names models.

export interface BenchmarkRunEstimateInput {
	/** Model x KV-cache combinations. One for the single-run control. */
	cellCount: number;
	/** Leaf task items — a NIAH generator's cases count individually, because a freeze fans out over each of them. */
	leafItemCount: number;
	repeatCount: number;
	warmup: boolean;
}

export interface BenchmarkRunEstimate extends BenchmarkRunEstimateInput {
	/** Runs one item of one cell costs: the measured repeats plus the warm-up, which is a run even though it never ranks. */
	runsPerItem: number;
	totalRuns: number;
	/** Null when the project has no completed run to extrapolate from — omitted rather than guessed. */
	estimatedMs: number | null;
	/** The node refuses the whole freeze above `MaxRunsPerRequest`, so this is a refusal, not a warning. */
	exceedsCap: boolean;
}

export function benchmarkRunEstimate(input: BenchmarkRunEstimateInput, medianRunMs: number | null): BenchmarkRunEstimate {
	const runsPerItem = Math.max(input.repeatCount, 0) + (input.warmup ? 1 : 0);
	const totalRuns = Math.max(input.cellCount, 0) * Math.max(input.leafItemCount, 0) * runsPerItem;
	return {
		...input,
		runsPerItem,
		totalRuns,
		// ponytail: runs x median, ignoring that a cold first load is slower than the rest. A per-cell load estimate
		// would need the model footprint and the host's disk speed; upgrade when an operator says the figure misleads.
		estimatedMs: medianRunMs === null ? null : totalRuns * medianRunMs,
		exceedsCap: totalRuns > benchmarkTaskItemLimits.maxRunsPerRequest,
	};
}

/**
 * The project's own median completed run, which is the only honest basis for "how long will this take on this box".
 * Warm-ups are excluded: they are the slow launch the repeats after them are measured without.
 */
export function medianBenchmarkRunDurationMs(runs: readonly BenchmarkRunSummary[]): number | null {
	const durations = runs
		.filter((run) => !run.isWarmup && run.primaryStatus === "Succeeded" && run.durationMs !== null)
		.map((run) => run.durationMs as number)
		.sort((left, right) => left - right);
	if (durations.length === 0) {
		return null;
	}
	const middle = Math.floor(durations.length / 2);
	return durations.length % 2 === 1
		? (durations[middle] as number)
		: ((durations[middle - 1] as number) + (durations[middle] as number)) / 2;
}

/** `1h 12m`, `12m 30s`, `45s` — coarse on purpose: an extrapolation is not precise enough to print seconds of an hour. */
export function formatBenchmarkDuration(estimatedMs: number | null): string | null {
	if (estimatedMs === null || !Number.isFinite(estimatedMs) || estimatedMs < 0) {
		return null;
	}
	const totalSeconds = Math.round(estimatedMs / 1000);
	const hours = Math.floor(totalSeconds / 3600);
	const minutes = Math.floor((totalSeconds % 3600) / 60);
	const seconds = totalSeconds % 60;
	if (hours > 0) {
		return `${hours}h ${minutes}m`;
	}
	return minutes > 0 ? `${minutes}m ${seconds}s` : `${seconds}s`;
}
