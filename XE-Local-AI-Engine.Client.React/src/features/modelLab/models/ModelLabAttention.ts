import type {
	XeLocalAiEngineClientEndpointsModelLabV1ModelLabAttentionHeadResponse as AttentionHead,
	XeLocalAiEngineClientEndpointsModelLabV1ModelLabAttentionResponse as AttentionResponse,
	XeLocalAiEngineClientServicesModelLabModelLabTraceResult as TraceResult,
} from "@/core/api/generated/types.gen";

// The map's column bound: past it every cell would be a sliver, and the SVG holds one rect per cell.
export const maxAttentionColumns = 256;

// Weight: a stored weight above 0. Floored: stored, but under 1/10,000. NotTopK: an earlier key the run did not keep, part of
// the rest. Future: after the query, causally never read. Missing: the whole row was not kept (the byte cap cut it).
export type AttentionCellState = "weight" | "floored" | "notTopK" | "future" | "missing";

export interface AttentionCell {
	x: string;
	y: string;
	value: number | null;
	state: AttentionCellState;
}

export interface AttentionMatrix {
	// Key positions, ascending.
	columns: number[];
	// Positions with a stored weight that did not fit under maxAttentionColumns.
	droppedColumns: number;
	// The largest weight in view; the color scale's top.
	max: number;
	cells: AttentionCell[];
}

export interface AttentionKey {
	position: number;
	weight: number;
	text: string | undefined;
	before: string | undefined;
	after: string | undefined;
}

// The token at a key position: a prompt piece before P, generated token (position − P) from P on; undefined where the
// result cut its text or the position is outside the run.
export function keyText(result: TraceResult, position: number): string | undefined {
	const prompt = result.promptTokens ?? 0;
	return position < prompt ? result.promptPieces?.[position]?.text : result.tokens?.[position - prompt]?.text;
}

// One row's stored keys for one head in position order, each with one neighbour per side as context. The row of query q
// covers keys 0..q only, so the key at q has no right neighbour.
export function rowKeys(result: TraceResult, head: AttentionHead, queryPosition: number): AttentionKey[] {
	return head.keys
		.map((position, at) => ({ position, weight: head.weights[at] ?? 0 }))
		.sort((left, right) => left.position - right.position)
		.map(({ position, weight }) => ({
			position,
			weight,
			text: keyText(result, position),
			before: position > 0 ? keyText(result, position - 1) : undefined,
			after: position < queryPosition ? keyText(result, position + 1) : undefined,
		}));
}

// Rows are the predicted tokens 0..steps−1, columns the key positions with a stored weight in any row for this head, the
// largest summed weights kept when there are more than maxAttentionColumns.
export function attentionMatrix(response: AttentionResponse, head: number, steps: number): AttentionMatrix {
	const rows = new Map(
		response.rows.map((row) => {
			const stored = row.heads[head];
			const weights = new Map(stored?.keys.map((position, at) => [position, stored.weights[at] ?? 0]) ?? []);
			return [row.step, { queryPosition: row.queryPosition, weights }];
		}),
	);
	const sums = new Map<number, number>();
	for (const row of rows.values()) {
		for (const [position, weight] of row.weights) {
			sums.set(position, (sums.get(position) ?? 0) + weight);
		}
	}
	const columns = [...sums]
		.sort((left, right) => right[1] - left[1] || left[0] - right[0])
		.slice(0, maxAttentionColumns)
		.map(([position]) => position)
		.sort((left, right) => left - right);
	let max = 0;
	const cells: AttentionCell[] = [];
	for (let step = 0; step < steps; step++) {
		const row = rows.get(step);
		for (const position of columns) {
			const weight = row?.weights.get(position);
			const state: AttentionCellState =
				row === undefined
					? "missing"
					: position > row.queryPosition
						? "future"
						: weight === undefined
							? "notTopK"
							: weight > 0
								? "weight"
								: "floored";
			if (state === "weight" && weight !== undefined) {
				max = Math.max(max, weight);
			}
			cells.push({ x: String(position), y: String(step), value: state === "weight" ? (weight ?? null) : null, state });
		}
	}
	return { columns, droppedColumns: sums.size - columns.length, max, cells };
}

// Query heads in order of their key/value head, as grouped-query attention shares them: head h reads kv head h / (H / Hkv).
export function kvHeadOf(head: number, heads: number, kvHeads: number): number {
	return kvHeads > 0 && heads >= kvHeads ? Math.floor(head / (heads / kvHeads)) : 0;
}
