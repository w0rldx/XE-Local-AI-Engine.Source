import { describe, expect, it } from "vitest";

import { compareInspections } from "@/features/models/models/GgufInspectComparison";

function tensor(name: string, ggmlTypeId = 12, byteSize: number | null = 100, ggmlType: string | null = "Q4_K") {
	return { name, shape: [256], ggmlType, ggmlTypeId, offset: 0, byteSize };
}

function inspection(tensors: ReturnType<typeof tensor>[], nonBlockTensors: ReturnType<typeof tensor>[] = []) {
	return { blocks: [{ index: 0, byteSize: null, tensors }], nonBlockTensors };
}

describe("compareInspections", () => {
	it("finds nothing changed between identical files", () => {
		const file = inspection([tensor("blk.0.a")], [tensor("token_embd")]);

		const comparison = compareInspections(file, file);

		expect(comparison).toMatchObject({
			bytesA: 200,
			bytesB: 200,
			difference: 0,
			changedTypeCount: 0,
			onlyInACount: 0,
			onlyInBCount: 0,
		});
		expect(comparison.groups.map((group) => [group.blockIndex, group.changed.length])).toEqual([
			[null, 0],
			[0, 0],
		]);
	});

	it("reports a type change with the size difference, as B minus A", () => {
		const comparison = compareInspections(
			inspection([tensor("blk.0.a", 12, 400), tensor("blk.0.b", 12, 100)]),
			inspection([tensor("blk.0.a", 11, 300, "Q3_K"), tensor("blk.0.b", 12, 100)]),
		);

		expect(comparison).toMatchObject({ bytesA: 500, bytesB: 400, difference: -100, changedTypeCount: 1 });
		const [group] = comparison.groups;
		expect(group).toMatchObject({ difference: -100 });
		expect(group?.changed.map((row) => [row.name, row.typeChanged, row.a?.ggmlType, row.b?.ggmlType])).toEqual([
			["blk.0.a", true, "Q4_K", "Q3_K"],
		]);
	});

	it("keeps a tensor found in only one file, in the block it was found in, and does not call it a type change", () => {
		const comparison = compareInspections(
			inspection([tensor("blk.0.a"), tensor("blk.0.only_a")]),
			inspection([tensor("blk.0.a"), tensor("blk.0.only_b", 12, 50)]),
		);

		expect(comparison).toMatchObject({
			bytesA: 200,
			bytesB: 150,
			difference: -50,
			changedTypeCount: 0,
			onlyInACount: 1,
			onlyInBCount: 1,
		});
		expect(comparison.groups[0]?.changed.map((row) => [row.name, row.a === null, row.b === null])).toEqual([
			["blk.0.only_a", false, true],
			["blk.0.only_b", true, false],
		]);
	});

	it("makes no difference up from an unknown size, and leaves other groups' figures alone", () => {
		const comparison = compareInspections(
			inspection([tensor("blk.0.a", 99, null, null)], [tensor("token_embd")]),
			inspection([tensor("blk.0.a", 12, 100)], [tensor("token_embd")]),
		);

		expect(comparison).toMatchObject({ bytesA: null, bytesB: 200, difference: null, changedTypeCount: 1 });
		expect(comparison.groups.map((group) => [group.blockIndex, group.difference])).toEqual([
			[null, 0],
			[0, null],
		]);
	});
});
