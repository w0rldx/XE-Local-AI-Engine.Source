import { describe, expect, it } from "vitest";

import { attentionMatrix, keyText, kvHeadOf, maxAttentionColumns, rowKeys } from "@/features/modelLab/models/ModelLabAttention";

const result = {
	promptTokens: 2,
	promptPieces: [
		{ id: 1, text: "a" },
		{ id: 2, text: "b" },
	],
	tokens: [
		{ id: 3, text: "c" },
		{ id: 4, text: "d" },
	],
};

const response = (rows: { step: number; queryPosition: number; keys: number[]; weights: number[] }[]) => ({
	layer: 3,
	heads: 1,
	kvHeads: 1,
	topKeys: 2,
	truncated: false,
	droppedRecords: 0,
	rows: rows.map(({ step, queryPosition, keys, weights }) => ({ step, queryPosition, heads: [{ keys, weights, rest: 0 }] })),
});

describe("ModelLabAttention", () => {
	it("reads a key position below P as a prompt piece and from P on as a generated token", () => {
		expect([0, 1, 2, 3, 4].map((position) => keyText(result, position))).toEqual(["a", "b", "c", "d", undefined]);
	});

	it("lists a row's keys in position order with one neighbour each side, none past the query", () => {
		const keys = rowKeys(result, { keys: [2, 0], weights: [0.6, 0.3], rest: 0.1 }, 2);

		expect(keys).toEqual([
			{ position: 0, weight: 0.3, text: "a", before: undefined, after: "b" },
			{ position: 2, weight: 0.6, text: "c", before: "b", after: undefined },
		]);
	});

	it("gives each cell its own state: weight, floored, not in the top K, future and a missing row", () => {
		const matrix = attentionMatrix(
			response([
				{ step: 0, queryPosition: 1, keys: [0, 1], weights: [0.5, 0] },
				{ step: 1, queryPosition: 2, keys: [2], weights: [0.25] },
			]),
			0,
			3,
		);

		expect(matrix.columns).toEqual([0, 1, 2]);
		expect(matrix.max).toBe(0.5);
		expect(matrix.cells.map((cell) => `${cell.y}:${cell.x}:${cell.state}`)).toEqual([
			"0:0:weight",
			"0:1:floored",
			"0:2:future",
			"1:0:notTopK",
			"1:1:notTopK",
			"1:2:weight",
			"2:0:missing",
			"2:1:missing",
			"2:2:missing",
		]);
		expect(matrix.cells.filter((cell) => cell.state !== "weight").every((cell) => cell.value === null)).toBe(true);
	});

	it("keeps the columns with the largest summed weight past the cap, in position order, and counts the rest", () => {
		const count = maxAttentionColumns + 10;
		// Position p weighs p, so the 10 lowest positions drop out.
		const keys = Array.from({ length: count }, (_, position) => position);
		const matrix = attentionMatrix(
			response([{ step: 0, queryPosition: count, keys, weights: keys.map((position) => position / 10_000) }]),
			0,
			1,
		);

		expect(matrix.columns).toHaveLength(maxAttentionColumns);
		expect(matrix.columns[0]).toBe(10);
		expect(matrix.columns.at(-1)).toBe(count - 1);
		expect(matrix.droppedColumns).toBe(10);
	});

	it("maps query heads to their key/value head in groups", () => {
		expect([0, 1, 2, 3, 4, 5, 6, 7].map((head) => kvHeadOf(head, 8, 2))).toEqual([0, 0, 0, 0, 1, 1, 1, 1]);
	});
});
