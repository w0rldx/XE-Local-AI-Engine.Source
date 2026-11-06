import type { TFunction } from "i18next";

import type {
	XeLocalAiEngineClientServicesModelLabModelLabArchitectureBlock as ArchitectureBlock,
	XeLocalAiEngineClientServicesModelLabModelLabArchitectureGroup as ArchitectureGroup,
	XeLocalAiEngineClientServicesModelLabModelLabArchitectureTensor as ArchitectureTensor,
	XeLocalAiEngineClientServicesModelLabModelLabArchitectureTemplate as ArchitectureTemplate,
	XeLocalAiEngineClientEndpointsModelLabV1ModelLabTraceRecordResponse as TraceRecord,
} from "@/core/api/generated/types.gen";
import { type BlockKind, blockKindColors } from "@/core/models/inspect/GgufInspectInsights";

export type { ArchitectureBlock, ArchitectureGroup, ArchitectureTemplate, ArchitectureTensor };

// The overview's selection: a block by its index, or the tensors outside every block.
export type ArchitectureSelection = number | "outside";

export interface BlockTraceSummary {
	// Mean of the residual's norm over the traced tokens; null when no token kept a finite one.
	meanNorm: number | null;
	maxP99: number | null;
	// The dimensions that show up most often among the tokens' largest values, most frequent first.
	topDimensions: number[];
}

const topDimensionCount = 5;

// One summary per traced block. A trace is keyed by the checkpoint's layer index, which pairing makes the GGUF block index.
export function summarizeTraceByBlock(
	records: readonly Pick<TraceRecord, "layer" | "norm" | "percentiles" | "topIndices">[],
): Map<number, BlockTraceSummary> {
	const byLayer = new Map<number, typeof records>();
	for (const record of records) {
		byLayer.set(record.layer, [...(byLayer.get(record.layer) ?? []), record]);
	}
	const summaries = new Map<number, BlockTraceSummary>();
	for (const [layer, layerRecords] of byLayer) {
		const norms = layerRecords.flatMap((record) => (record.norm === null ? [] : [record.norm]));
		const p99s = layerRecords.flatMap((record) => {
			const p99 = record.percentiles[2];
			return p99 === null || p99 === undefined ? [] : [p99];
		});
		const counts = new Map<number, number>();
		for (const index of layerRecords.flatMap((record) => record.topIndices)) {
			counts.set(index, (counts.get(index) ?? 0) + 1);
		}
		summaries.set(layer, {
			meanNorm: norms.length > 0 ? norms.reduce((sum, norm) => sum + norm, 0) / norms.length : null,
			maxP99: p99s.length > 0 ? Math.max(...p99s) : null,
			topDimensions: [...counts.entries()]
				.sort(([leftIndex, left], [rightIndex, right]) => right - left || leftIndex - rightIndex)
				.slice(0, topDimensionCount)
				.map(([index]) => index),
		});
	}
	return summaries;
}

export function componentLabel(t: TFunction, component: string): string {
	const labels: Record<string, string> = {
		attention: t("pages.modelLab.architecture.component.attention", "Attention"),
		embedding: t("pages.modelLab.architecture.component.embedding", "Embedding"),
		ffn: t("pages.modelLab.architecture.component.ffn", "Feed-forward"),
		mtp: t("pages.modelLab.architecture.component.mtp", "Multi-token prediction"),
		norm: t("pages.modelLab.architecture.component.norm", "Norm"),
		other: t("pages.modelLab.architecture.component.other", "Other"),
		output: t("pages.modelLab.architecture.component.output", "Output"),
		ssm: t("pages.modelLab.architecture.component.ssm", "Linear attention (SSM)"),
	};
	return labels[component] ?? component;
}

// How a GGUF value follows from the Hugging Face value it was converted from.
export function valueRelationLabel(t: TFunction, relation: string | null): string {
	const labels: Record<string, string> = {
		equal: t("pages.modelLab.architecture.relation.equal", "Same values as the Hugging Face tensor"),
		neg_exp: t("pages.modelLab.architecture.relation.negExp", "Minus the exponential of the Hugging Face value"),
		plus_one: t("pages.modelLab.architecture.relation.plusOne", "The Hugging Face value plus one"),
		quantized: t("pages.modelLab.architecture.relation.quantized", "Quantized from the Hugging Face values"),
		reordered: t(
			"pages.modelLab.architecture.relation.reordered",
			"Value heads reordered from the Hugging Face tensor, so values sit in another order",
		),
	};
	return relation === null ? t("pages.modelLab.architecture.relation.notStated", "Not stated") : (labels[relation] ?? relation);
}

// The descriptor's kind as the Inspect panel's colours and labels know it.
export function architectureBlockKind(kind: string): BlockKind {
	return kind in blockKindColors ? (kind as BlockKind) : "other";
}

// "0–2, 5, 7–8": runs of consecutive indices collapse into ranges, in ascending order.
export function formatBlockRanges(indices: Iterable<number>): string {
	const sorted = [...new Set(indices)].sort((left, right) => left - right);
	const ranges: string[] = [];
	for (let start = 0; start < sorted.length; ) {
		let end = start;
		while (end + 1 < sorted.length && sorted[end + 1] === (sorted[end] ?? 0) + 1) {
			end += 1;
		}
		ranges.push(start === end ? String(sorted[start]) : `${sorted[start]}–${sorted[end]}`);
		start = end + 1;
	}
	return ranges.join(", ");
}

// A measured value in the page's language, not the browser's: "3.86" on an English page, "3,86" on a German one.
export function formatTraceValue(value: number | null, language: string | undefined): string {
	if (value === null) {
		return "–";
	}
	try {
		return value.toLocaleString(language, { maximumFractionDigits: 2 });
	} catch {
		// A malformed stored tag (`en_US`) is a RangeError; the environment's default is a worse number, not a broken page.
		return value.toLocaleString(undefined, { maximumFractionDigits: 2 });
	}
}

// Enough of a trace's prompt to tell two traces of one model apart in a picker.
export function promptExcerpt(prompt: string): string {
	const line = prompt.replace(/\s+/g, " ").trim();
	return line.length > 40 ? `${line.slice(0, 40)}…` : line;
}

// A template node names a component; the group it stands for is that component in the same block.
export function componentIdFor(blockIndex: number, component: string): string {
	return `blk.${blockIndex}.${component}`;
}

// The server's default when no layers are sent (ModelLabTraceSpec.LayersFor), shown so the operator knows what it picks.
export function defaultTraceLayers(blockCount: number): number[] {
	return [...new Set([Math.floor(blockCount / 4), Math.floor(blockCount / 2), Math.floor((blockCount * 3) / 4)])];
}
