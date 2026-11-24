import { describe, expect, it } from "vitest";

import {
	applyModelLabPatch,
	jumpPatch,
	type ModelLabSearch,
	modelLabSearchSchema,
	sameExperimentId,
} from "@/features/modelLab/models/ModelLabNavigation";

const traceA = "11111111-1111-4111-8111-111111111111";
const traceB = "22222222-2222-4222-8222-222222222222";
const linked: ModelLabSearch = { model: "a", block: 3, trace: traceA, level: "math", experiment: traceA };

describe("modelLabSearchSchema", () => {
	it("keeps every valid field", () => {
		expect(modelLabSearchSchema.parse({ ...linked, tab: "trace" })).toEqual({ ...linked, tab: "trace" });
	});

	it("drops each invalid field alone and keeps the rest", () => {
		expect(
			modelLabSearchSchema.parse({
				tab: "bogus",
				model: "",
				block: -1,
				trace: "not-a-guid",
				experiment: 7,
				level: "deep",
			}),
		).toEqual({});
		expect(modelLabSearchSchema.parse({ model: "a", block: 1.5, level: "detail" })).toEqual({ model: "a", level: "detail" });
	});

	it.each([[0], [Number.MAX_SAFE_INTEGER], ["embedding"], ["output"], ["other"], ["stack"], ["mtp"], ["0-2"], ["10-2147483647"]])(
		"takes block %s",
		(block) => {
			expect(modelLabSearchSchema.parse({ block }).block).toBe(block);
		},
	);

	it.each([
		[-1],
		[0.5],
		[""],
		["3"],
		[[1, 2]],
		[Number.MAX_SAFE_INTEGER + 2],
		// Past a block index's int range, leading zeros, reversed, a single block, signs and blanks.
		["0-2147483648"],
		["0-99999999999"],
		["01-2"],
		["0-02"],
		["2-1"],
		["2-2"],
		["-1-2"],
		["1-"],
		["1--2"],
		[" 0-2"],
	])("drops block %j", (block) => {
		expect(modelLabSearchSchema.parse({ block }).block).toBeUndefined();
	});

	it("drops a malformed range alone and keeps the other fields", () => {
		expect(modelLabSearchSchema.parse({ model: "a", block: "3-1", trace: traceA, level: "math" })).toEqual({
			model: "a",
			trace: traceA,
			level: "math",
		});
	});

	it("keeps the block detail's operation, group and tensor, real dotted ids included", () => {
		const inside = { op: "attn_q", group: "blk.0.attention", tensor: "blk.0.attn_q.weight" };
		expect(modelLabSearchSchema.parse({ ...linked, ...inside })).toEqual({ ...linked, ...inside });
		// A tensor name may take the reader's whole 256 bytes, in two-byte characters too.
		expect(modelLabSearchSchema.parse({ tensor: "ä".repeat(128) }).tensor).toBe("ä".repeat(128));
		expect(modelLabSearchSchema.parse({ group: "g".repeat(256) }).group).toBe("g".repeat(256));
	});

	it.each([
		["op", ""],
		["op", "o".repeat(257)],
		["op", 3],
		["group", ""],
		["group", "g".repeat(257)],
		["group", ["blk.0.attention"]],
		["tensor", ""],
		["tensor", "t".repeat(257)],
		// 129 characters, but 258 UTF-8 bytes: past what the reader takes.
		["tensor", "ä".repeat(129)],
	])("drops %s %j alone and keeps the rest", (field, value) => {
		const inside = { op: "attn", group: "blk.0.attention", tensor: "blk.0.attn_q.weight" };
		const { [field as keyof typeof inside]: _dropped, ...kept } = inside;
		expect(modelLabSearchSchema.parse({ ...linked, ...inside, [field]: value })).toEqual({ ...linked, ...kept });
	});
});

describe("applyModelLabPatch", () => {
	it("keeps an omitted field and removes one set to undefined", () => {
		expect(applyModelLabPatch(linked, { level: undefined, tab: "inspect" })).toEqual({
			model: "a",
			block: 3,
			trace: traceA,
			experiment: traceA,
			tab: "inspect",
		});
	});

	it("stores the default tab and level as absent", () => {
		expect(applyModelLabPatch({ tab: "trace", level: "math" }, { tab: "architecture", level: "overview" })).toEqual({});
	});

	it("drops the block and trace when another model is picked, and keeps the rest", () => {
		expect(applyModelLabPatch(linked, { model: "b" })).toEqual({ model: "b", level: "math", experiment: traceA });
		expect(applyModelLabPatch(linked, { model: undefined })).toEqual({ level: "math", experiment: traceA });
	});

	it("keeps the block and trace when the same model is picked again", () => {
		expect(applyModelLabPatch(linked, { model: "a" })).toEqual(linked);
	});

	it("keeps a cross-model jump's own block, block 0 included, and trace", () => {
		expect(
			applyModelLabPatch(linked, jumpPatch({ tab: "architecture", modelName: "b", traceId: traceB, blockIndex: 0 })),
		).toEqual({ model: "b", block: 0, trace: traceB, level: "math", experiment: traceA });
	});

	it("drops the last model's block and trace on a Try in Next token jump to another model, and keeps no prompt", () => {
		expect(
			applyModelLabPatch(linked, jumpPatch({ tab: "nextToken", modelName: "b", prompt: "secret", disableThinking: true })),
		).toEqual({ tab: "nextToken", model: "b", level: "math", experiment: traceA });
	});

	describe("the block detail's selection", () => {
		const inside = { op: "attn", group: "blk.3.attention", tensor: "blk.3.attn_q.weight" };
		const opened: ModelLabSearch = { ...linked, ...inside };

		it("drops it on another model and on another block, the whole model included", () => {
			expect(applyModelLabPatch(opened, { model: "b" })).toEqual({ model: "b", level: "math", experiment: traceA });
			expect(applyModelLabPatch(opened, { block: 4 })).toEqual({ ...linked, block: 4 });
			expect(applyModelLabPatch(opened, { block: "0-3" })).toEqual({ ...linked, block: "0-3" });
			const { block: _block, ...root } = linked;
			expect(applyModelLabPatch(opened, { block: undefined })).toEqual(root);
		});

		it("keeps it for the same block and model, a level, tab or trace change, and changes only what a patch names", () => {
			expect(applyModelLabPatch(opened, { block: 3, model: "a" })).toEqual(opened);
			expect(applyModelLabPatch(opened, { level: "detail", tab: "trace", trace: traceB })).toEqual({
				...opened,
				level: "detail",
				tab: "trace",
				trace: traceB,
			});
			expect(applyModelLabPatch(opened, { op: "ffn", group: "blk.3.ffn", tensor: undefined })).toEqual({
				...linked,
				op: "ffn",
				group: "blk.3.ffn",
			});
		});

		it("keeps a jump's own values while it drops the last block's", () => {
			expect(applyModelLabPatch(opened, { model: "b", block: 0, op: "ffn", group: "blk.0.ffn" })).toEqual({
				model: "b",
				block: 0,
				op: "ffn",
				group: "blk.0.ffn",
				level: "math",
				experiment: traceA,
			});
			expect(
				applyModelLabPatch(opened, jumpPatch({ tab: "architecture", modelName: "a", traceId: traceB, blockIndex: 0 })),
			).toEqual({ ...linked, block: 0, trace: traceB });
		});
	});

	it("computes a patch from the latest search", () => {
		const closeIfOpen = (removed: string) => (previous: ModelLabSearch) =>
			sameExperimentId(previous.experiment, removed) ? { experiment: undefined } : {};

		expect(applyModelLabPatch(linked, closeIfOpen(traceA.toUpperCase())).experiment).toBeUndefined();
		expect(applyModelLabPatch(linked, closeIfOpen(traceB)).experiment).toBe(traceA);
	});
});
