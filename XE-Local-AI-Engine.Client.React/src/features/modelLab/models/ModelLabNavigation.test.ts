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

	it("computes a patch from the latest search", () => {
		const closeIfOpen = (removed: string) => (previous: ModelLabSearch) =>
			sameExperimentId(previous.experiment, removed) ? { experiment: undefined } : {};

		expect(applyModelLabPatch(linked, closeIfOpen(traceA.toUpperCase())).experiment).toBeUndefined();
		expect(applyModelLabPatch(linked, closeIfOpen(traceB)).experiment).toBe(traceA);
	});
});
