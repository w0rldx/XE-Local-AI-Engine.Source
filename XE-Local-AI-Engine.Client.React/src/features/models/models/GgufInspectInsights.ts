import type { XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufTensorDto } from "@/core/api/generated";

type TensorDto = XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufTensorDto;

export type BlockKind = "fullAttention" | "linearAttention" | "mtp" | "other";

// First match wins: an MTP block is also a full-attention block, and a linear block also carries attn_qkv.
const kindPatterns: readonly (readonly [BlockKind, RegExp])[] = [
	["mtp", /\.nextn\./],
	["linearAttention", /\.ssm_/],
	["fullAttention", /\.attn_q\./],
];

export function blockKind(tensors: readonly TensorDto[]): BlockKind {
	const match = kindPatterns.find(([, pattern]) => tensors.some((tensor) => pattern.test(tensor.name)));
	return match ? match[0] : "other";
}

// Experts are their own axis: a hybrid MoE model keeps its linear and full blocks, and may have dense blocks too.
export function hasExperts(tensors: readonly TensorDto[]): boolean {
	return tensors.some((tensor) => /\.ffn_[^.]+_exps\./.test(tensor.name));
}

export interface TensorTypeRow {
	ggmlType: string | null;
	ggmlTypeId: number;
	tensorCount: number;
	// The sum of the known sizes; unknownSizeCount says how many of the tensors it leaves out.
	bytes: number;
	unknownSizeCount: number;
	// Share of all known bytes, 0-100.
	percent: number;
}

// One row per ggml type, largest first. A tensor of an unknown type keeps its own row under its raw id.
export function tensorTypeRows(tensors: readonly TensorDto[]): TensorTypeRow[] {
	const rows = new Map<number, TensorTypeRow>();
	for (const tensor of tensors) {
		const row = rows.get(tensor.ggmlTypeId) ?? {
			ggmlType: tensor.ggmlType,
			ggmlTypeId: tensor.ggmlTypeId,
			tensorCount: 0,
			bytes: 0,
			unknownSizeCount: 0,
			percent: 0,
		};
		row.tensorCount += 1;
		row.bytes += tensor.byteSize ?? 0;
		row.unknownSizeCount += tensor.byteSize === null ? 1 : 0;
		rows.set(tensor.ggmlTypeId, row);
	}
	const sorted = [...rows.values()].sort((left, right) => right.bytes - left.bytes);
	const total = sorted.reduce((sum, row) => sum + row.bytes, 0);
	return sorted.map((row) => ({ ...row, percent: total > 0 ? (row.bytes / total) * 100 : 0 }));
}
