import type { TFunction } from "i18next";

import type { XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufTensorDto } from "@/core/api/generated";
import { humanizeBytes } from "@/core/formatting/BytesFormatting";

type TensorDto = XeLocalAiEngineClientEndpointsLocalModelsV1ModelGgufTensorDto;

export type BlockKind = "fullAttention" | "linearAttention" | "mtp" | "other";

// First match wins: an MTP block is also a full-attention block, and a linear block also carries attn_qkv, which outside
// a linear block is full attention fused into one tensor.
const kindPatterns: readonly (readonly [BlockKind, RegExp])[] = [
	["mtp", /\.nextn\./],
	["linearAttention", /\.ssm_/],
	["fullAttention", /\.attn_(?:q|qkv)\./],
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

export function tensorSizeLabel(t: TFunction, bytes: number | null | undefined): string {
	return bytes === null || bytes === undefined ? t("pages.models.details.unknown", "Unknown") : humanizeBytes(bytes);
}

export function tensorTypeLabel(t: TFunction, ggmlType: string | null, ggmlTypeId: number): string {
	return ggmlType ?? t("pages.models.inspect.unknownType", "type {{id}}", { id: ggmlTypeId });
}

// "blk.12.attn_q.weight" reads as "attn_q.weight" inside its own block row.
export function shortTensorName(name: string, blockIndex: number | undefined): string {
	const prefix = blockIndex === undefined ? "" : `blk.${blockIndex}.`;
	return prefix && name.startsWith(prefix) ? name.slice(prefix.length) : name;
}
