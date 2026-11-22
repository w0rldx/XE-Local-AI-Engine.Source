import { describe, expect, it } from "vitest";

import {
	activeJobPollMs,
	candidateNote,
	stepperBlocks,
	toStepEntry,
	toTraceTable,
} from "@/features/modelLab/models/ModelLabTrace";

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

describe("candidateNote", () => {
	const kept = { id: 1, text: "a", candidates: [{ id: 1, text: "a", logprob: -0.1 }], processedCandidates: [] };
	const none = { id: 2, text: "b", candidates: null, processedCandidates: null };

	it("calls a step with its own candidates kept, whatever the count says", () => {
		expect(candidateNote(kept, 5, 2)).toBe("kept");
		expect(candidateNote(kept, 0, undefined)).toBe("kept");
	});

	it("calls a step past the kept count not kept, and one without a count from before this version", () => {
		expect(candidateNote(none, 2, 2)).toBe("notKept");
		expect(candidateNote(none, 1, null)).toBe("beforeVersion");
		expect(candidateNote(none, 1, undefined)).toBe("beforeVersion");
		expect(candidateNote(none, 1, 2)).toBe("beforeVersion");
	});
});

describe("toStepEntry", () => {
	it("still reads a step saved before candidateSteps existed, whose arrays are all there", () => {
		const entry = toStepEntry(
			{ id: 1, text: "a", logprob: -0.1, candidates: [{ id: 1, text: "a", logprob: -0.1 }], processedCandidates: [] },
			"raw",
		);
		expect(entry?.alternatives).toEqual([{ id: 1, text: "a", logprob: -0.1 }]);
	});
});
