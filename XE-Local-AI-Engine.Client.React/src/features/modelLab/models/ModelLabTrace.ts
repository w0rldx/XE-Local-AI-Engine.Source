import type {
	XeLocalAiEngineClientServicesModelLabModelLabResultToken as ResultToken,
	XeLocalAiEngineClientEndpointsModelLabV1ModelLabTraceRecordResponse as TraceRecord,
} from "@/core/api/generated/types.gen";

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
export function toTraceTable(
	records: Pick<TraceRecord, "step" | "layer" | "kind" | "norm">[],
	tokens: ResultToken[],
): { layers: TraceLayer[]; rows: TraceRow[] } {
	const layers = new Map<number, string>();
	const steps = new Map<number, Map<number, number | null>>();
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
