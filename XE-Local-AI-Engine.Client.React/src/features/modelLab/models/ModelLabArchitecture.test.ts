import { describe, expect, it } from "vitest";

import {
	defaultTraceLayers,
	formatBlockRanges,
	formatTraceValue,
	promptExcerpt,
	summarizeTraceByBlock,
} from "@/features/modelLab/models/ModelLabArchitecture";
import { layoutTemplate } from "@/features/modelLab/models/ModelLabTemplateLayout";

describe("summarizeTraceByBlock", () => {
	it("averages finite norms, takes the highest p99 and ranks top dimensions by how often they appear", () => {
		const summaries = summarizeTraceByBlock([
			{ layer: 6, norm: 1, percentiles: [0, 0, 4], topIndices: [5, 2] },
			{ layer: 6, norm: null, percentiles: [0, 0, null], topIndices: [2, 9] },
			{ layer: 6, norm: 3, percentiles: [0, 0, 8], topIndices: [2, 5] },
			{ layer: 12, norm: null, percentiles: [null, null, null], topIndices: [] },
		]);

		expect(summaries.get(6)).toEqual({ meanNorm: 2, maxP99: 8, topDimensions: [2, 5, 9] });
		expect(summaries.get(12)).toEqual({ meanNorm: null, maxP99: null, topDimensions: [] });
		expect(summaries.has(18)).toBe(false);
	});
});

describe("formatBlockRanges", () => {
	it("collapses consecutive blocks into ranges, in ascending order, once each", () => {
		expect(formatBlockRanges([8, 0, 1, 2, 5, 7, 2])).toBe("0–2, 5, 7–8");
		expect(formatBlockRanges([15])).toBe("15");
		expect(formatBlockRanges(Array.from({ length: 24 }, (_, index) => index))).toBe("0–23");
		expect(formatBlockRanges([])).toBe("");
	});
});

describe("formatTraceValue", () => {
	it("formats in the page's language, and a missing value as a dash", () => {
		expect(formatTraceValue(3.856, "en")).toBe("3.86");
		expect(formatTraceValue(3.856, "de")).toBe("3,86");
		expect(formatTraceValue(null, "de")).toBe("–");
		expect(formatTraceValue(3.5, "en_US")).toBe((3.5).toLocaleString(undefined, { maximumFractionDigits: 2 }));
	});
});

describe("promptExcerpt", () => {
	it("keeps a short prompt on one line and cuts a long one", () => {
		expect(promptExcerpt("  Why is\nthe sky  blue? ")).toBe("Why is the sky blue?");
		expect(promptExcerpt("x".repeat(50))).toBe(`${"x".repeat(40)}…`);
	});
});

describe("defaultTraceLayers", () => {
	it("names the server's quarter points, once each", () => {
		expect(defaultTraceLayers(24)).toEqual([6, 12, 18]);
		expect(defaultTraceLayers(2)).toEqual([0, 1]);
	});
});

describe("layoutTemplate", () => {
	it("ranks a node below everything that feeds it and places every node of a cycle once", () => {
		const positions = layoutTemplate(
			["in", "norm", "attn", "add", "loop"],
			[
				{ from: "in", to: "norm" },
				{ from: "norm", to: "attn" },
				{ from: "in", to: "add" },
				{ from: "attn", to: "add" },
				{ from: "loop", to: "loop" },
			],
		);

		expect(positions.get("in")?.y).toBe(0);
		expect(positions.get("add")?.y).toBeGreaterThan(positions.get("attn")?.y ?? Number.POSITIVE_INFINITY);
		expect(positions.size).toBe(5);
	});
});
