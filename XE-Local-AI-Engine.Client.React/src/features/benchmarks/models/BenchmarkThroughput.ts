import type { BenchmarkEvidenceEntry } from "@/features/benchmarks/models/BenchmarkLaunchEvidence";
import type { BenchmarkRunSummary, BenchmarkRunThroughput } from "@/features/benchmarks/models/BenchmarkModels";

// One vocabulary for the throughput numbers, shared by the runs table, the run pane and the compare view, so the three
// can never disagree about what "tok/s" means. tg is DECODE speed and pp is PREFILL speed — the node reports them
// separately because the blended figure they replaced conflated the two. Display only: none of this ranks a run.

/** `24.3` → `"24.3 tok/s"`, absent → `"—"`. */
export const formatTokensPerSecond = (value: number | null): string => (value === null ? "—" : `${value.toFixed(1)} tok/s`);

/** Sub-second latencies read better in ms; anything longer reads better in seconds. */
export function formatLatencyMs(value: number | null): string {
	if (value === null) {
		return "—";
	}
	return value < 1000 ? `${Math.round(value)} ms` : `${(value / 1000).toFixed(2)} s`;
}

/** `null` when the run carries no split at all, so a caller can hide the whole block rather than render six dashes. */
export function hasThroughputBreakdown(throughput: BenchmarkRunThroughput): boolean {
	return (
		throughput.ttftMs !== null ||
		throughput.promptTokens !== null ||
		throughput.promptTokensPerSecond !== null ||
		throughput.generationTokens !== null ||
		throughput.generationTokensPerSecond !== null ||
		throughput.cachedPromptTokens !== null ||
		throughput.segmentCount !== null
	);
}

/**
 * The comparable throughput facts of one run, in the same shape the launch-evidence diff already consumes — so the
 * compare view reuses {@link diffLaunchEvidence} and its table instead of growing a second diff implementation.
 */
export function throughputEvidenceEntries(throughput: BenchmarkRunThroughput): BenchmarkEvidenceEntry[] {
	return Object.entries(throughput).map(([key, value]) => ({ key: `throughput.${key}`, value }));
}

// Repeat statistics stay client-side over loaded runs, avoiding a second arithmetic source of truth.
// They are display-only and never affect ranking.

/** One measurement's central tendency and spread. `stdDev` is the SAMPLE deviation (n-1), which needs n >= 2. */
export interface BenchmarkStatSummary {
	mean: number;
	stdDev: number;
	count: number;
}

/** The spread of one repeat cohort's throughput. A member is null when no run in the cohort reported it. */
export interface BenchmarkRepeatStats {
	tokensPerSecond: BenchmarkStatSummary | null;
	promptTokensPerSecond: BenchmarkStatSummary | null;
	ttftMs: BenchmarkStatSummary | null;
}

/**
 * What makes two runs comparable measurements of the same thing: the same model BUILD, the same KV-cache type, the
 * same effective launch identity, and the same repeat MODE. Two runs of one model on different launch arguments are two different experiments,
 * and averaging them would report a spread that is really a configuration difference. The NAME is not the build — a
 * model deleted and reinstalled, or a repo tag repointed at new weights, keeps its name and changes its content
 * fingerprint — so the fingerprint is what keeps two builds out of one mean. Falls back to the INTENDED identity while
 * a run has not launched yet, and to the empty string when neither is recorded (legacy rows), which groups legacy rows
 * of one model+KV together rather than scattering them.
 */
export function benchmarkRepeatCohortKey(
	run: Pick<BenchmarkRunSummary, "primaryModelName" | "modelContentFingerprint" | "primaryLaunch" | "repeatMode">,
): string {
	const { kvCacheType, effectiveLaunchIdentity, intendedLaunchIdentity } = run.primaryLaunch;
	return [
		run.primaryModelName,
		run.modelContentFingerprint,
		kvCacheType ?? "",
		effectiveLaunchIdentity ?? intendedLaunchIdentity ?? "",
		// The repeat MODE is part of the experiment, not a detail of it. A throughput group is deterministic and its
		// spread is the machine; an answer-variance group samples and its spread is the model. One mean over both
		// would describe neither, and the runs are otherwise identical in every member above.
		run.repeatMode,
	].join("|");
}

function summarize(values: readonly number[]): BenchmarkStatSummary | null {
	if (values.length === 0) {
		return null;
	}
	const mean = values.reduce((total, value) => total + value, 0) / values.length;
	// Sample (n-1) deviation, not population: these are repeated samples of a process, not the whole population of its
	// runs. With a single sample there is no spread to report, and n-1 would divide by zero.
	const stdDev =
		values.length < 2
			? 0
			: Math.sqrt(values.reduce((total, value) => total + (value - mean) ** 2, 0) / (values.length - 1));
	return { mean, stdDev, count: values.length };
}

const finite = (value: number | null): value is number => value !== null && Number.isFinite(value);

/**
 * Throughput spread per repeat cohort, keyed by {@link benchmarkRepeatCohortKey}. Only SUCCEEDED, non-warm-up runs are
 * counted: a failed or cancelled run has no measurement to average, and a warm-up is the first-launch cost the repeats
 * after it were meant not to pay — including it would report the spread of the thing being controlled for.
 */
export function benchmarkRepeatStats(runs: readonly BenchmarkRunSummary[]): Map<string, BenchmarkRepeatStats> {
	const cohorts = new Map<string, BenchmarkRunSummary[]>();
	for (const run of runs) {
		if (run.isWarmup || run.primaryStatus !== "Succeeded") {
			continue;
		}
		const key = benchmarkRepeatCohortKey(run);
		const existing = cohorts.get(key);
		if (existing) {
			existing.push(run);
		} else {
			cohorts.set(key, [run]);
		}
	}
	return new Map(
		[...cohorts].map(([key, cohort]) => [
			key,
			{
				tokensPerSecond: summarize(cohort.map((run) => run.tokensPerSecond).filter(finite)),
				promptTokensPerSecond: summarize(cohort.map((run) => run.throughput.promptTokensPerSecond).filter(finite)),
				ttftMs: summarize(cohort.map((run) => run.throughput.ttftMs).filter(finite)),
			},
		]),
	);
}

/** `82.1 ± 1.4 (n=3)`. Null below two samples: "± 0 (n=1)" states a certainty a single reading does not have. */
export function formatStatSummary(summary: BenchmarkStatSummary | null, digits = 1): string | null {
	return summary === null || summary.count < 2
		? null
		: `${summary.mean.toFixed(digits)} ± ${summary.stdDev.toFixed(digits)} (n=${summary.count})`;
}
