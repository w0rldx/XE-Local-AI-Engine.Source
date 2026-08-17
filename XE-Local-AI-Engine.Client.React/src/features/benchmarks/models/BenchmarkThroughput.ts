import type { BenchmarkEvidenceEntry } from "@/features/benchmarks/models/BenchmarkLaunchEvidence";
import type { BenchmarkRunThroughput } from "@/features/benchmarks/models/BenchmarkModels";

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
		throughput.cachedPromptTokens !== null
	);
}

/**
 * The comparable throughput facts of one run, in the same shape the launch-evidence diff already consumes — so the
 * compare view reuses {@link diffLaunchEvidence} and its table instead of growing a second diff implementation.
 */
export function throughputEvidenceEntries(throughput: BenchmarkRunThroughput): BenchmarkEvidenceEntry[] {
	return Object.entries(throughput).map(([key, value]) => ({ key: `throughput.${key}`, value }));
}
