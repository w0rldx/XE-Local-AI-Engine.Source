import { describe, expect, it } from "vitest";

import type { ArchitectureBlock, ArchitectureGroup } from "@/features/modelLab/models/ModelLabArchitecture";
import {
	buildArchitectureHierarchy,
	chipFor,
	compareChipBlocks,
	vocabularySize,
} from "@/features/modelLab/models/ModelLabHierarchy";

type Values = NonNullable<Parameters<typeof buildArchitectureHierarchy>[0]["hyperparameters"]>;

const header = (
	blockCount: number | null,
	nextnPredictLayers: number | null = null,
	splitCount: number | null = null,
): Values => ({
	fileType: "Q4_K_M",
	declaredParamCount: null,
	blockCount,
	nextnPredictLayers,
	splitCount,
	attentionHeadCount: null,
	attentionHeadCountKv: null,
	attentionKeyLength: null,
	attentionValueLength: null,
	embeddingLength: null,
	contextLength: null,
	expertCount: null,
	expertUsedCount: null,
	fullAttentionInterval: null,
});

const tensor = (ggufName: string, shape = [64, 64], ggmlType: string | null = "Q4_K") => ({
	ggufName,
	hfName: null,
	shape,
	ggmlType,
	byteSize: null,
	valueRelation: null,
});

const kinds: Record<string, string> = { L: "linearAttention", F: "fullAttention", M: "mtp", O: "other" };

function block(index: number, kind: string, tensors = [tensor(`blk.${index}.attn_q.weight`)]): ArchitectureBlock {
	return {
		index,
		kind,
		byteSize: null,
		groups: [{ componentId: `blk.${index}.attention`, component: "attention", provenance: "read", tensors }],
	};
}

// "LLLF" from index 0, or the given indices.
function blocks(pattern: string, indices?: number[]): ArchitectureBlock[] {
	return [...pattern].map((letter, at) => block(indices?.[at] ?? at, kinds[letter] ?? "other"));
}

const group = (componentId: string, component = componentId): ArchitectureGroup => ({
	componentId,
	component,
	provenance: "read",
	tensors: [tensor(`${componentId}.weight`)],
});
const ends = [group("embedding"), group("output_norm", "norm"), group("output")];

function hierarchyOf(
	trunk: ArchitectureBlock[],
	values: Values | null,
	nonBlock: ArchitectureGroup[] = ends,
	support = "detailed",
) {
	const built = buildArchitectureHierarchy({ support, blocks: trunk, nonBlock, hyperparameters: values });
	if (built === null) {
		throw new Error("a readable file builds a hierarchy");
	}
	return built;
}

const selections = (built: ReturnType<typeof hierarchyOf>) =>
	built.segments.map((segment) => segment.chips.map((chip) => chip.selection));

describe("buildArchitectureHierarchy", () => {
	it("cuts a Qwen3.5 trunk into its 8 repetitions of 3 linear + 1 full, keeps MTP apart and states the pattern", () => {
		const trunk = [...blocks("LLLF".repeat(8)), block(32, "mtp")];
		const built = hierarchyOf(trunk, header(33, 1));

		expect(selections(built)).toEqual([["0-3", "4-7", "8-11", "12-15", "16-19", "20-23", "24-27", "28-31"]]);
		expect(built.segments[0]?.chips[0]?.runs).toEqual([
			{ kind: "linearAttention", count: 3 },
			{ kind: "fullAttention", count: 1 },
		]);
		expect(built.mtp.map((candidate) => candidate.index)).toEqual([32]);
		expect(built.trunkBlocks).toHaveLength(32);
		expect(built.trunkComplete).toBe(true);
		expect(built.gapAfter).toBe(false);
		expect(built.pattern).toEqual({
			unit: [
				{ kind: "linearAttention", count: 3 },
				{ kind: "fullAttention", count: 1 },
			],
			repeats: 8,
		});
		expect(built.outside).toEqual(["embedding", "output"]);
	});

	it("makes one chip of a uniform trunk, with no pattern to state", () => {
		const built = hierarchyOf(blocks("F".repeat(16)), header(16));

		expect(selections(built)).toEqual([["0-15"]]);
		expect(built.segments[0]?.chips[0]?.runs).toEqual([{ kind: "fullAttention", count: 16 }]);
		expect(built.trunkComplete).toBe(true);
		expect(built.pattern).toBeNull();
	});

	it("falls back to one chip per run when a unit leaves a remainder, a one-block run being the block itself", () => {
		const built = hierarchyOf(blocks("LLLFLLLFLL"), header(10));

		expect(selections(built)).toEqual([["0-2", 3, "4-6", 7, "8-9"]]);
		expect(built.pattern).toBeNull();
	});

	it("keeps two MTP blocks in their own group", () => {
		const built = hierarchyOf([...blocks("FFFF"), block(4, "mtp"), block(5, "mtp")], header(6, 2));

		expect(built.mtp.map((candidate) => candidate.index)).toEqual([4, 5]);
		expect(selections(built)).toEqual([["0-3"]]);
		expect(built.trunkComplete).toBe(true);
	});

	it("treats an MTP block between trunk blocks as a boundary and a missing index as a gap", () => {
		const interposed = hierarchyOf([...blocks("FF"), block(2, "mtp"), ...blocks("FF", [3, 4])], header(5, 1));
		expect(selections(interposed)).toEqual([["0-1"], ["3-4"]]);
		expect(interposed.segments.map((segment) => segment.gapBefore)).toEqual([false, false]);

		const missing = hierarchyOf(blocks("FFFF", [0, 1, 3, 4]), header(5));
		expect(selections(missing)).toEqual([["0-1"], ["3-4"]]);
		expect(missing.segments.map((segment) => segment.gapBefore)).toEqual([false, true]);
		expect(missing.trunkComplete).toBe(false);
	});

	it.each([
		["right before an MTP block", [block(0, "fullAttention"), block(2, "mtp"), block(3, "fullAttention")]],
		["right after an MTP block", [block(0, "fullAttention"), block(1, "mtp"), block(3, "fullAttention")]],
	])("marks a missing index %s as a gap", (_case, file) => {
		const built = hierarchyOf(file, header(4, 1));

		expect(selections(built)).toEqual([[0], [3]]);
		expect(built.segments.map((segment) => segment.gapBefore)).toEqual([false, true]);
	});

	it("marks the blocks after the last one in the file when an MTP block sits between trunk blocks", () => {
		const built = hierarchyOf([block(0, "fullAttention"), block(1, "mtp"), block(2, "fullAttention")], header(4, 1));

		expect(built.gapAfter).toBe(true);
		expect(built.trunkComplete).toBe(false);
	});

	it.each([
		["with its MTP block", [...blocks("F".repeat(64)), block(64, "mtp")]],
		["without its MTP block", blocks("F".repeat(64))],
	])("calls a 64-block trunk declared with one MTP block complete and gapless %s", (_case, file) => {
		const built = hierarchyOf(file, header(65, 1));

		expect(built.gapAfter).toBe(false);
		expect(built.trunkComplete).toBe(true);
	});

	it("marks the trunk blocks a file without its MTP block also lacks", () => {
		const built = hierarchyOf(blocks("F".repeat(63)), header(65, 1));

		expect(built.gapAfter).toBe(true);
		expect(built.trunkComplete).toBe(false);
	});

	it("counts the MTP blocks in the file when the header leaves the MTP layer count out", () => {
		const built = hierarchyOf([...blocks("FFFF"), block(4, "mtp")], header(5, null));

		expect(built.gapAfter).toBe(false);
		expect(built.trunkComplete).toBe(true);
	});

	it.each([
		["a prefix shard", [0, 1, 2, 3, 4, 5, 6, 7], { before: false, after: true }],
		["a leading-missing shard", [8, 9, 10, 11, 12, 13, 14, 15], { before: true, after: false }],
		["a middle shard", [4, 5, 6, 7, 8, 9, 10, 11], { before: true, after: true }],
	])("states no pattern for %s, and marks the blocks it lacks", (_case, indices, gaps) => {
		const built = hierarchyOf(blocks("LLLFLLLF", indices), header(16));

		expect(built.segments[0]?.gapBefore).toBe(gaps.before);
		expect(built.gapAfter).toBe(gaps.after);
		expect(built.trunkComplete).toBe(false);
		expect(built.pattern).toBeNull();
		// The chips still follow the unit inside the shard.
		expect(built.segments[0]?.chips).toHaveLength(2);
	});

	it.each([
		["a split file", header(8, null, 2), ends, "detailed"],
		["a file without an embedding", header(8), ends.slice(1), "detailed"],
		["a file without an output group", header(8), ends.slice(0, 1), "detailed"],
		["an overview file", header(8), ends, "overview"],
		["a file whose header lacks the block count", header(null), ends, "detailed"],
	])("calls the trunk of %s incomplete and states no pattern", (_case, values, nonBlock, support) => {
		const built = hierarchyOf(blocks("LLLFLLLF"), values, nonBlock, support);

		expect(built.trunkComplete).toBe(false);
		expect(built.pattern).toBeNull();
	});

	it("holds nothing for a tensorless shard", () => {
		const built = hierarchyOf([], header(24, null, 3), []);

		expect(built.segments).toEqual([]);
		expect(built.trunkBlocks).toEqual([]);
		expect(built.mtp).toEqual([]);
		expect(built.outside).toEqual([]);
		expect(built.gapAfter).toBe(false);
	});

	it("files a group of an unknown componentId under Other, and a final norm alone under Output", () => {
		expect(hierarchyOf([], null, [group("rope_freqs", "other")]).outside).toEqual(["other"]);
		expect(hierarchyOf([], null, [group("output_norm", "norm")]).outside).toEqual(["output"]);
		expect(hierarchyOf([], null, [group("other"), group("embedding")]).outside).toEqual(["embedding", "other"]);
	});

	it("builds nothing for an unreadable file", () => {
		expect(buildArchitectureHierarchy({ support: "unreadable", blocks: [], nonBlock: [], hyperparameters: null })).toBeNull();
	});

	it("finds the chip a block belongs to or a token names", () => {
		const built = hierarchyOf(blocks("LLLFLLLF"), header(8));

		expect(chipFor(built, 5)?.selection).toBe("4-7");
		expect(chipFor(built, "0-3")?.selection).toBe("0-3");
		expect(chipFor(built, "1-3")).toBeUndefined();
		expect(chipFor(built, "stack")).toBeUndefined();
	});
});

describe("compareChipBlocks", () => {
	const mixed = (index: number, outType = "Q6_K", order: "qk" | "kq" = "qk") => {
		const q = tensor(`blk.${index}.attn_q.weight`, [64, 64], "Q4_K");
		const k = tensor(`blk.${index}.attn_norm.weight`, [64], "F32");
		const out = tensor(`blk.${index}.attn_output.weight`, [64, 64], outType);
		return block(index, "fullAttention", order === "qk" ? [q, k, out] : [out, k, q]);
	};

	it("finds the same layout and stored types in blocks whose tensors are listed in another order", () => {
		expect(compareChipBlocks([mixed(0), mixed(1, "Q6_K", "kq"), mixed(2)])).toEqual({
			layoutCompared: 2,
			typesCompared: 2,
			layoutDiffers: [],
			typesDiffer: [],
			typeUnknown: false,
		});
	});

	it("names a block where one corresponding tensor has another stored type", () => {
		expect(compareChipBlocks([mixed(0), mixed(1, "Q4_K"), mixed(2)]).typesDiffer).toEqual([1]);
	});

	it("says a stored type is unknown instead of comparing types", () => {
		expect(
			compareChipBlocks([mixed(0), block(1, "fullAttention", [tensor("blk.1.attn_q.weight", [64, 64], null)])]),
		).toMatchObject({
			typeUnknown: true,
		});
	});

	it("names a block whose tensor shape or set differs, and compares each block with the first of its own kind", () => {
		const reshaped = block(1, "fullAttention", [tensor("blk.1.attn_q.weight", [64, 32])]);
		const linear = block(2, "linearAttention", [tensor("blk.2.ssm_out.weight")]);
		const linearToo = block(3, "linearAttention", [tensor("blk.3.ssm_out.weight")]);

		expect(compareChipBlocks([block(0, "fullAttention"), reshaped, linear, linearToo]).layoutDiffers).toEqual([1]);
	});

	it("compares nothing in a chip whose blocks are each the only one of their kind", () => {
		const chip = [block(0, "linearAttention"), block(1, "fullAttention")];

		expect(compareChipBlocks(chip)).toMatchObject({ layoutCompared: 0, typesCompared: 0, layoutDiffers: [], typesDiffer: [] });
	});

	it("compares the layout but no stored types when two blocks of one kind share no tensor", () => {
		const disjoint = [block(0, "fullAttention"), block(1, "fullAttention", [tensor("blk.1.attn_k.weight")])];

		expect(compareChipBlocks(disjoint)).toMatchObject({
			layoutCompared: 1,
			typesCompared: 0,
			layoutDiffers: [1],
			typesDiffer: [],
		});
	});
});

describe("vocabularySize", () => {
	const embedding = (shape: number[]) => ({
		nonBlock: [{ ...group("embedding"), tensors: [tensor("token_embd.weight", shape)] }],
	});

	it.each([
		["a rank-2 embedding's second axis", [1024, 151936], 151936],
		["nothing for a rank-1 tensor", [1024], null],
		["nothing for a rank-3 tensor", [4, 1024, 32], null],
	])("reads %s", (_case, shape, expected) => {
		expect(vocabularySize(embedding(shape))).toBe(expected);
	});

	it("reads nothing without a token embedding", () => {
		expect(vocabularySize({ nonBlock: [group("output")] })).toBeNull();
	});
});
