import type { BenchmarkEvidenceEntry } from "@/features/benchmarks/models/BenchmarkLaunchEvidence";
import type { BenchmarkRunFidelity, BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";

// One vocabulary for the quant-fidelity numbers, shared by the runs table, the fidelity panel and the compare view.
// Perplexity and KL divergence measure how far a quantized build drifted from the weights it was made from; both are
// DISPLAY ONLY and neither is ever a ranking input (plan §2 #4). Nothing here interprets a number as "better".

/** A perplexity reading is a mean over chunks and the standard error of that mean; one without the other is not a measurement. */
export function formatPerplexity(fidelity: Pick<BenchmarkRunFidelity, "perplexityMean" | "perplexityStdErr">): string | null {
	const { perplexityMean, perplexityStdErr } = fidelity;
	if (perplexityMean === null) {
		return null;
	}
	// Four decimals because the whole point is separating two quants of one model: the live pair on this box differ by
	// 0.152 with standard errors of ~0.075, and rounding to two would print their bands as touching when they do not.
	return perplexityStdErr === null
		? perplexityMean.toFixed(4)
		: `${perplexityMean.toFixed(4)} ± ${perplexityStdErr.toFixed(4)}`;
}

/** `0.9421` → `"94.2 %"`. The share of tokens where the quant's most likely token is the base model's. */
export const formatTopTokenAgreement = (value: number | null): string | null =>
	value === null ? null : `${(value * 100).toFixed(1)} %`;

export const formatKldValue = (value: number | null): string | null => (value === null ? null : value.toFixed(4));

/**
 * Whether the KLD trio may be RENDERED AS NUMBERS. The node already withholds them unless the run's stored base-logit
 * digest is the one the project currently expects (plan §2 #14 / R3), and this repeats the gate on the reading side so
 * a future contract that sends a figure alongside a non-`ok` state still cannot leak one into a comparison. A stale
 * measurement gets a badge, never a greyed-out figure — a number a reader can still see is a number they will compare.
 */
export const isKldComparable = (fidelity: Pick<BenchmarkRunFidelity, "kldState">): boolean => fidelity.kldState === "ok";

/** Nothing measured yet: the cell renders a dash rather than an empty statistics block. */
export const hasFidelityNumbers = (fidelity: BenchmarkRunFidelity): boolean =>
	fidelity.perplexityMean !== null || (isKldComparable(fidelity) && fidelity.kldMean !== null);

/**
 * The comparable fidelity facts of one run, in the shape the launch-evidence diff already consumes — so the N-way
 * compare reuses that one diff engine rather than growing a second. The KLD trio is omitted entirely when the run's
 * measurement is not comparable: a compare table is exactly where a stale figure would do its damage.
 */
export function fidelityEvidenceEntries(fidelity: BenchmarkRunFidelity | null): BenchmarkEvidenceEntry[] {
	if (fidelity === null) {
		return [];
	}
	const comparable = isKldComparable(fidelity);
	return [
		{ key: "fidelity.status", value: fidelity.status },
		{ key: "fidelity.perplexityMean", value: fidelity.perplexityMean },
		{ key: "fidelity.perplexityStdErr", value: fidelity.perplexityStdErr },
		{ key: "fidelity.perplexityChunks", value: fidelity.perplexityChunks },
		{ key: "fidelity.perplexityContextTokens", value: fidelity.perplexityContextTokens },
		{ key: "fidelity.perplexityCorpusId", value: fidelity.perplexityCorpusId },
		{ key: "fidelity.kldState", value: fidelity.kldState },
		{ key: "fidelity.kldMean", value: comparable ? fidelity.kldMean : null },
		{ key: "fidelity.kldP99", value: comparable ? fidelity.kldP99 : null },
		{ key: "fidelity.topTokenAgreement", value: comparable ? fidelity.topTokenAgreement : null },
	];
}

/**
 * Whether re-measuring this run is a request the node can act on. Fidelity replays the run's own frozen placement, so
 * there is nothing to replay until the primary succeeded, and a measurement already in flight would only queue a
 * second attempt behind itself.
 */
export const canMeasureFidelity = (run: Pick<BenchmarkRunSummary, "primaryStatus" | "fidelity">): boolean =>
	run.primaryStatus === "Succeeded" && run.fidelity?.status !== "queued" && run.fidelity?.status !== "running";
