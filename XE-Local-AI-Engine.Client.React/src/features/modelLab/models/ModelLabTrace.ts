import type {
	XeLocalAiEngineClientServicesModelLabModelLabResultToken as ResultToken,
	XeLocalAiEngineClientEndpointsModelLabV1ModelLabTraceRecordResponse as TraceRecord,
} from "@/core/api/generated/types.gen";
import type { NextTokenStepEntry } from "@/features/modelLab/models/NextTokenSampling";

const activeJobFallbackMs = 5_000;

// Safety net while the hub is live, for an event that never arrived.
const activeJobPushFloorMs = 60_000;

// While a job runs, a hub that is down must not strand the page on it, and a live one must not be polled over.
export function activeJobPollMs(isLive: boolean): number {
	return isLive ? activeJobPushFloorMs : activeJobFallbackMs;
}

export interface TraceLayer {
	index: number;
	// F for a full-attention block, L for a linear one, ? when the model does not say.
	kind: string;
}

export interface TraceRow {
	step: number;
	// Undefined when the token's text was not saved with the trace.
	token: string | undefined;
	// The residual's norm at each layer, in the order of `layers`; null where the cap dropped the record or the value was not finite.
	norms: (number | null)[];
}

// Pivots the file's one record per token per layer into one row per token, so a token reads across its layers.
// Every generated token gets a row: one past the trace's byte cap has no records, and its candidates stay selectable.
export function toTraceTable(
	records: Pick<TraceRecord, "step" | "layer" | "kind" | "norm">[],
	tokens: ResultToken[],
): { layers: TraceLayer[]; rows: TraceRow[] } {
	const layers = new Map<number, string>();
	const steps = new Map<number, Map<number, number | null>>(tokens.map((_, step) => [step, new Map()]));
	for (const record of records) {
		const step = record.step ?? 0;
		const layer = record.layer ?? 0;
		layers.set(layer, record.kind ?? "?");
		const norms = steps.get(step) ?? new Map<number, number | null>();
		norms.set(layer, record.norm ?? null);
		steps.set(step, norms);
	}

	const orderedLayers = [...layers.entries()].sort(([a], [b]) => a - b).map(([index, kind]) => ({ index, kind }));
	const rows = [...steps.entries()]
		.sort(([a], [b]) => a - b)
		.map(([step, norms]) => ({
			step,
			token: tokens[step]?.text ?? undefined,
			norms: orderedLayers.map((layer) => norms.get(layer.index) ?? null),
		}));
	return { layers: orderedLayers, rows };
}

// Raw: the model's own logits. Processed: after the checkpoint's generation-config processors, which chose the token.
export type CandidateBasis = "raw" | "processed";

// A step's exact candidates in the shape the next-token step view reads; undefined in a trace made before they were recorded.
export function toStepEntry(token: ResultToken, basis: CandidateBasis): NextTokenStepEntry | undefined {
	const candidates = basis === "raw" ? token.candidates : token.processedCandidates;
	if (!candidates) {
		return undefined;
	}
	// Null where the value was not finite: a token a processor masked, so probability 0.
	const logprob = (basis === "raw" ? token.logprob : token.processedLogprob) ?? Number.NEGATIVE_INFINITY;
	return {
		id: token.id,
		text: token.text,
		logprob,
		isReasoning: false,
		alternatives: candidates.map((candidate) => ({
			id: candidate.id,
			text: candidate.text,
			logprob: candidate.logprob ?? Number.NEGATIVE_INFINITY,
		})),
	};
}

// True when the processors changed nothing among the recorded candidates of this step.
export function sameCandidates(token: ResultToken): boolean {
	const raw = token.candidates ?? [];
	const processed = token.processedCandidates ?? [];
	return (
		token.logprob === token.processedLogprob &&
		raw.length === processed.length &&
		raw.every((candidate, index) => candidate.id === processed[index]?.id && candidate.logprob === processed[index]?.logprob)
	);
}
