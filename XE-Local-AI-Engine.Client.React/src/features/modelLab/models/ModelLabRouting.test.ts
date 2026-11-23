import { describe, expect, it } from "vitest";

import { expertChoices, expertUsage } from "@/features/modelLab/models/ModelLabRouting";

const block = (layer: number, experts: number[], weights: number[]) => ({ layer, experts, weights });

describe("ModelLabRouting", () => {
	it("orders one block's chosen experts by weight, the lower id first on a tie, keeping the exact weights", () => {
		expect(expertChoices(block(0, [40, 3, 12, 7], [0.0625, 0.3125, 0.3125, 0.140625]))).toEqual([
			{ expert: 3, weight: 0.3125 },
			{ expert: 12, weight: 0.3125 },
			{ expert: 7, weight: 0.140625 },
			{ expert: 40, weight: 0.0625 },
		]);
	});

	it("counts one pick per step for each expert the block chose, most picked first, over the given steps only", () => {
		const steps = [
			{ step: 0, blocks: [block(0, [1, 2], [0.5, 0.25]), block(1, [9, 8], [0.5, 0.5])] },
			{ step: 1, blocks: [block(0, [2, 5], [0.5, 0.25]), block(1, [9, 7], [0.5, 0.5])] },
			{ step: 2, blocks: [block(0, [5, 2], [0.5, 0.25]), block(1, [6, 7], [0.5, 0.5])] },
		];

		expect(expertUsage(steps, 0)).toEqual([
			{ expert: 2, picks: 3 },
			{ expert: 5, picks: 2 },
			{ expert: 1, picks: 1 },
		]);
		expect(expertUsage(steps, 1).map((usage) => usage.picks)).toEqual([2, 2, 1, 1]);
		expect(expertUsage(steps, 1)[0]?.expert).toBe(7);
		expect(expertUsage(steps.slice(0, 1), 0)).toEqual([
			{ expert: 1, picks: 1 },
			{ expert: 2, picks: 1 },
		]);
		expect(expertUsage(steps, 4)).toEqual([]);
	});
});
