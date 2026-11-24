import type { XeLocalAiEngineClientServicesModelLabModelLabArchitectureResponse as ArchitectureResponse } from "@/core/api/generated/types.gen";
import {
	type ArchitectureBlock,
	type ArchitectureSelection,
	type BlockRangeToken,
	type OutsideEnd,
	outsideGroups,
} from "@/features/modelLab/models/ModelLabArchitecture";

// Consecutive blocks of one kind.
export interface BlockRun {
	kind: string;
	count: number;
}

// One chip of the overview row: a repetition of the segment's unit, else one run, in index order.
export interface BlockChip {
	first: number;
	last: number;
	// Its range token, or the block's own index for a one-block chip (which is shown as the block itself).
	selection: number | BlockRangeToken;
	blocks: ArchitectureBlock[];
	runs: BlockRun[];
}

// Trunk blocks with consecutive indices. `gapBefore`: an index just before it is not in the file at all; an MTP block
// between two segments is a boundary, not a gap.
export interface TrunkSegment {
	gapBefore: boolean;
	chips: BlockChip[];
}

// "3 linear + 1 full, 8 times": the unit of runs the whole trunk repeats.
export interface BlockPattern {
	unit: BlockRun[];
	repeats: number;
}

export interface ArchitectureHierarchy {
	// The groups outside the blocks the file holds, in row order.
	outside: OutsideEnd[];
	segments: TrunkSegment[];
	// Block indices the header declares after the highest one in the file, of any kind, other than MTP blocks it lacks.
	gapAfter: boolean;
	trunkBlocks: ArchitectureBlock[];
	// Multi-token prediction blocks: their own group, never chained after the trunk.
	mtp: ArchitectureBlock[];
	// Every condition of a whole, unsplit trunk except a template: `detailed`, one segment from 0 to the header's last
	// trunk index, at most one split part, an embedding and an output group. A graph adds `templates.length > 0`.
	trunkComplete: boolean;
	// Only on a complete trunk that repeats a unit.
	pattern: BlockPattern | null;
}

type HierarchySource = Pick<ArchitectureResponse, "support" | "blocks" | "nonBlock" | "hyperparameters">;

function runsOf(blocks: readonly ArchitectureBlock[]): BlockRun[] {
	const runs: BlockRun[] = [];
	for (const block of blocks) {
		const run = runs.at(-1);
		if (run?.kind === block.kind) {
			run.count += 1;
		} else {
			runs.push({ kind: block.kind, count: 1 });
		}
	}
	return runs;
}

// The smallest unit of two or more runs that the whole list repeats at least twice, or null.
function repeatingUnit(runs: readonly BlockRun[]): number | null {
	for (let size = 2; size * 2 <= runs.length; size += 1) {
		if (
			runs.length % size === 0 &&
			runs.every((run, at) => run.kind === runs[at % size]?.kind && run.count === runs[at % size]?.count)
		) {
			return size;
		}
	}
	return null;
}

function chipOf(blocks: ArchitectureBlock[]): BlockChip {
	const first = blocks[0]?.index ?? 0;
	const last = blocks.at(-1)?.index ?? first;
	return { first, last, selection: first === last ? first : `${first}-${last}`, blocks, runs: runsOf(blocks) };
}

function segmentChips(blocks: ArchitectureBlock[]): { chips: BlockChip[]; pattern: BlockPattern | null } {
	const runs = runsOf(blocks);
	const unit = repeatingUnit(runs);
	const chips: BlockChip[] = [];
	let start = 0;
	let end = 0;
	for (const [index, run] of runs.entries()) {
		end += run.count;
		// With a unit, a chip closes after each repetition; without one, after each run.
		if (unit === null || (index + 1) % unit === 0) {
			chips.push(chipOf(blocks.slice(start, end)));
			start = end;
		}
	}
	return { chips, pattern: unit === null ? null : { unit: runs.slice(0, unit), repeats: runs.length / unit } };
}

// What the file establishes about its own structure, nothing more. Null for an unreadable file.
export function buildArchitectureHierarchy(response: HierarchySource): ArchitectureHierarchy | null {
	if (response.support === "unreadable") {
		return null;
	}
	const sorted = [...response.blocks].sort((left, right) => left.index - right.index);
	const mtp: ArchitectureBlock[] = [];
	const trunkBlocks: ArchitectureBlock[] = [];
	const parts: { gapBefore: boolean; blocks: ArchitectureBlock[] }[] = [];
	let previous: ArchitectureBlock | undefined;
	// An index missing from the file since the last trunk block, over trunk and MTP indices together: trunk 0, missing 1,
	// MTP 2, trunk 3 is a gap, while an MTP block with no missing index beside it is only a boundary.
	let missing = false;
	for (const block of sorted) {
		missing ||= block.index !== (previous?.index ?? -1) + 1;
		if (block.kind === "mtp") {
			mtp.push(block);
		} else {
			trunkBlocks.push(block);
			const part = parts.at(-1);
			if (part !== undefined && previous === part.blocks.at(-1) && previous?.index === block.index - 1) {
				part.blocks.push(block);
			} else {
				parts.push({ gapBefore: missing, blocks: [block] });
			}
			missing = false;
		}
		previous = block;
	}
	const built = parts.map((part) => ({ gapBefore: part.gapBefore, ...segmentChips(part.blocks) }));
	const outside = (["embedding", "output", "other"] as const).filter((end) => outsideGroups(response.nonBlock, end).length > 0);
	const values = response.hyperparameters;
	// The MTP blocks the file holds count even when the header leaves `nextn_predict_layers` out.
	const mtpCount = Math.max(values?.nextnPredictLayers ?? 0, mtp.length);
	const declaredLast = values?.blockCount == null ? null : values.blockCount - mtpCount - 1;
	const lastIndex = trunkBlocks.at(-1)?.index;
	// Past the highest block of either kind, only the declared MTP blocks the file lacks may be missing: they come last.
	const highest = sorted.at(-1)?.index;
	const lacking = Math.max(0, (values?.nextnPredictLayers ?? 0) - mtp.length);
	const trunkComplete =
		response.support === "detailed" &&
		built.length === 1 &&
		trunkBlocks[0]?.index === 0 &&
		lastIndex === declaredLast &&
		(values?.splitCount == null || values.splitCount === 1) &&
		outside.includes("embedding") &&
		outside.includes("output");
	return {
		outside,
		segments: built.map(({ gapBefore, chips }) => ({ gapBefore, chips })),
		gapAfter: values?.blockCount != null && highest !== undefined && highest < values.blockCount - 1 - lacking,
		trunkBlocks,
		mtp,
		trunkComplete,
		pattern: trunkComplete ? (built[0]?.pattern ?? null) : null,
	};
}

export function allChips(hierarchy: ArchitectureHierarchy): BlockChip[] {
	return hierarchy.segments.flatMap((segment) => segment.chips);
}

// The chip a selection names, or the chip that holds the selected trunk block.
export function chipFor(hierarchy: ArchitectureHierarchy, selection: ArchitectureSelection | null): BlockChip | undefined {
	return allChips(hierarchy).find((chip) =>
		typeof selection === "number" ? chip.blocks.some((block) => block.index === selection) : chip.selection === selection,
	);
}

export interface ChipComparison {
	// Blocks compared with an earlier block of their kind; zero when every kind appears once, so nothing is claimed.
	layoutCompared: number;
	// Of those, blocks sharing at least one tensor signature with their reference, so their stored types were compared.
	typesCompared: number;
	// Blocks whose tensor layout differs from the first block of their kind in the chip.
	layoutDiffers: number[];
	// Blocks where a corresponding tensor is stored in another type than in the first block of their kind.
	typesDiffer: number[];
	// Some tensor's stored type is not known, so no type comparison is claimed.
	typeUnknown: boolean;
}

// A tensor's place in its block, whatever its order: component, the name after the exact `blk.N.` prefix, and shape.
function tensorTypes(block: ArchitectureBlock): Map<string, string | null> {
	const prefix = `blk.${block.index}.`;
	return new Map(
		block.groups.flatMap((group) =>
			group.tensors.map((tensor) => {
				const name = tensor.ggufName.startsWith(prefix) ? tensor.ggufName.slice(prefix.length) : tensor.ggufName;
				return [`${group.component}\u0000${name}\u0000${tensor.shape.join(",")}`, tensor.ggmlType] as const;
			}),
		),
	);
}

// Compares each block with the first block of the same kind: blocks of two kinds always differ, so that says nothing.
export function compareChipBlocks(blocks: readonly ArchitectureBlock[]): ChipComparison {
	const references = new Map<string, Map<string, string | null>>();
	const comparison: ChipComparison = {
		layoutCompared: 0,
		typesCompared: 0,
		layoutDiffers: [],
		typesDiffer: [],
		typeUnknown: false,
	};
	for (const block of blocks) {
		const types = tensorTypes(block);
		comparison.typeUnknown ||= [...types.values()].includes(null);
		const reference = references.get(block.kind);
		if (reference === undefined) {
			references.set(block.kind, types);
			continue;
		}
		comparison.layoutCompared += 1;
		if (reference.size !== types.size || [...types.keys()].some((key) => !reference.has(key))) {
			comparison.layoutDiffers.push(block.index);
		}
		const shared = [...types].filter(([key]) => reference.has(key));
		if (shared.length > 0) {
			comparison.typesCompared += 1;
		}
		if (shared.some(([key, type]) => reference.get(key) !== type)) {
			comparison.typesDiffer.push(block.index);
		}
	}
	return comparison;
}

// The vocabulary size from the embedding's shape (innermost first, so axis 1), only for a rank-2 `token_embd.weight`.
export function vocabularySize(response: Pick<ArchitectureResponse, "nonBlock">): number | null {
	const shape = response.nonBlock
		.flatMap((group) => group.tensors)
		.find((tensor) => tensor.ggufName === "token_embd.weight")?.shape;
	return shape?.length === 2 ? (shape[1] ?? null) : null;
}
