import { describe, expect, it } from "vitest";

import { activeJobPollMs, stepperBlocks, toTraceTable } from "@/features/modelLab/models/ModelLabTrace";

describe("activeJobPollMs", () => {
	it("keeps only a floor while the hub is live, and polls otherwise", () => {
		expect(activeJobPollMs(true)).toBe(60_000);
		expect(activeJobPollMs(false)).toBe(5_000);
	});
});

describe("toTraceTable", () => {
	it("reads one row per token across the layers, in order", () => {
		const { layers, rows } = toTraceTable(
			[
				{ step: 1, layer: 12, kind: "L", norm: 4 },
				{ step: 0, layer: 12, kind: "L", norm: 2 },
				{ step: 0, layer: 6, kind: "F", norm: 1 },
				{ step: 1, layer: 6, kind: "F", norm: 3 },
			],
			[{ text: "Hel" }, { text: "lo" }],
		);

		expect(layers).toEqual([
			{ index: 6, kind: "F" },
			{ index: 12, kind: "L" },
		]);
		expect(rows).toEqual([
			{ step: 0, token: "Hel", norms: [1, 2] },
			{ step: 1, token: "lo", norms: [3, 4] },
		]);
	});

	it("keeps a value that was not finite empty rather than reading it as zero", () => {
		const { rows } = toTraceTable([{ step: 0, layer: 6, kind: "F", norm: null }], [{ text: "Hi" }]);

		expect(rows).toEqual([{ step: 0, token: "Hi", norms: [null] }]);
	});

	it("marks what the byte cap dropped instead of inventing a value", () => {
		const { rows } = toTraceTable(
			[
				{ step: 0, layer: 6, kind: "F", norm: 1 },
				{ step: 0, layer: 12, kind: "L", norm: 2 },
				{ step: 1, layer: 6, kind: "F", norm: 3 },
			],
			[],
		);

		expect(rows[1]).toEqual({ step: 1, token: undefined, norms: [3, null] });
	});
});

describe("stepperBlocks", () => {
	const table = toTraceTable(
		[
			{ step: 0, layer: 1, kind: "L", norm: 2 },
			{ step: 0, layer: 3, kind: "F", norm: 5 },
			{ step: 1, layer: 1, kind: "L", norm: 4 },
		],
		[{ text: "a" }, { text: "b" }],
	);

	it("shows the traced blocks of a step when the result does not name every block", () => {
		expect(stepperBlocks(table, 1)).toEqual([
			{ index: 1, kind: "L", norm: 4 },
			{ index: 3, kind: "F", norm: null },
		]);
	});

	it("shows every block when the result names their kinds, leaving untraced ones without a norm", () => {
		expect(stepperBlocks(table, 0, ["L", "L", "L", "F"])).toEqual([
			{ index: 0, kind: "L", norm: undefined },
			{ index: 1, kind: "L", norm: 2 },
			{ index: 2, kind: "L", norm: undefined },
			{ index: 3, kind: "F", norm: 5 },
		]);
	});
});
