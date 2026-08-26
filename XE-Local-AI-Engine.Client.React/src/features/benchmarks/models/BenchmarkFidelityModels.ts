/** Chunks the node will score, and the bounds it refuses outside of. */
export const benchmarkFidelityChunkLimits = { min: 50, max: 655, default: 200 } as const;

/**
 * What the project measures beside the answer. Detail-only: the listing does not carry these, and the KLD half is
 * opt-in because the base-logit cache it needs is tens of gigabytes.
 */
export interface BenchmarkProjectFidelity {
	enabled: boolean;
	kldEnabled: boolean;
	/** What the operator picked, or null for the node's default. Edit this one. */
	chunks: number | null;
	/** What actually runs. Render this one — it resolves the null above. */
	chunksEffective: number;
	kldBaseModelName: string | null;
	/** Resolved server-side from the base model. Read-only: a caller-supplied value could make two figures compare that do not. */
	kldBaseFingerprint: string | null;
	/**
	 * The comparability digest the project currently expects. A run whose stored digest differs renders `kld-stale`.
	 * Null while KLD is off or no base is selected. Never recomputed client-side — the node owns this expression.
	 */
	kldExpectedDigest: string | null;
}

/**
 * The fidelity measurement's own lifecycle, verbatim from the wire. `skipped` is a real terminal answer, not a failure:
 * the project did not ask for a measurement, so there is nothing to report and nothing to fix.
 */
const benchmarkFidelityStatuses = ["queued", "running", "succeeded", "failed", "cancelled", "skipped"] as const;
export type BenchmarkFidelityStatus = (typeof benchmarkFidelityStatuses)[number];
/** An unrecognized status reads as `queued` — the node's own default for a fidelity row it has not terminalized. */
export const toBenchmarkFidelityStatus = (value: unknown): BenchmarkFidelityStatus =>
	benchmarkFidelityStatuses.find((status) => status === value) ?? "queued";

/**
 * Whether a run's KL-divergence figures are comparable against what the project currently expects. Three answers rather
 * than a boolean, because "never measured" and "measured against something else" are different facts and the UI says
 * different things: the first renders a dash, the second a `kld-stale` badge with a re-measure hint.
 */
const benchmarkFidelityKldStates = ["none", "ok", "kld-stale"] as const;
export type BenchmarkFidelityKldState = (typeof benchmarkFidelityKldStates)[number];
/** Fail-closed: an unknown state reads as "nothing measured", never as a comparable number. */
export const toBenchmarkFidelityKldState = (value: unknown): BenchmarkFidelityKldState =>
	benchmarkFidelityKldStates.find((state) => state === value) ?? "none";

/**
 * How far one quantized build drifted from the weights it was made from: perplexity over a fixed corpus at a pinned
 * 512-token window, and optionally KL divergence against a base model's logits. Both are DISPLAY ONLY and neither ever
 * enters `rank` — a quant that answers the frozen task better is the better quant regardless of how far its logits moved.
 *
 * Two perplexity numbers only compare when {@link BenchmarkRunFidelity.perplexityCorpusId} and
 * {@link BenchmarkRunFidelity.perplexityContextTokens} match, and the KLD trio is withheld by the node outright unless
 * {@link BenchmarkRunFidelity.kldState} is `ok`.
 */
export interface BenchmarkRunFidelity {
	status: BenchmarkFidelityStatus;
	/** The immutable attempt these numbers came from; a re-measure inserts a new one rather than overwriting. */
	attemptId: string | null;
	perplexityMean: number | null;
	/** Standard error of the mean over the scored chunks. Without it a perplexity difference cannot be read as real. */
	perplexityStdErr: number | null;
	perplexityChunks: number | null;
	/** Pinned to 512 by the node: perplexity is only comparable at a fixed window. */
	perplexityContextTokens: number | null;
	/** `wikitext2-raw-test@<sha256-12>` — two perplexity numbers compare only when this matches. */
	perplexityCorpusId: string | null;
	kldState: BenchmarkFidelityKldState;
	kldMean: number | null;
	kldP99: number | null;
	/** How often the quant's most likely token is the base model's, as a 0..1 fraction. */
	topTokenAgreement: number | null;
	/** The base model's content fingerprint. Evidence for the operator, NOT the comparability gate. */
	kldBaseFingerprint: string | null;
	errorMessage: string | null;
}
