// @vitest-environment jsdom

import { beforeEach, describe, expect, it } from "vitest";

import {
	clearImageFormOverrides,
	imageFormOverridesKeyPrefix,
	readImageFormOverrides,
	writeImageFormOverrides,
} from "@/features/images/models/ImageFormOverrides";

const key = `${imageFormOverridesKeyPrefix}m`;

describe("ImageFormOverrides", () => {
	beforeEach(() => localStorage.clear());

	it("round-trips a valid override and clears it", () => {
		writeImageFormOverrides("m", { steps: 30, cfgScale: 2.5, sampler: "euler" });
		expect(readImageFormOverrides("m")).toEqual({ steps: 30, cfgScale: 2.5, sampler: "euler" });

		clearImageFormOverrides("m");

		expect(readImageFormOverrides("m")).toBeNull();
	});

	it.each([
		["not JSON", "{"],
		["a non-object", "7"],
		["steps below the form minimum", JSON.stringify({ steps: 0, cfgScale: 2.5, sampler: "euler" })],
		["fractional steps", JSON.stringify({ steps: 2.5, cfgScale: 2.5, sampler: "euler" })],
		["CFG above the form maximum", JSON.stringify({ steps: 30, cfgScale: 31, sampler: "euler" })],
		["a non-numeric CFG", JSON.stringify({ steps: 30, cfgScale: "2.5", sampler: "euler" })],
		["an unknown sampler", JSON.stringify({ steps: 30, cfgScale: 2.5, sampler: "ddim" })],
		["a missing field", JSON.stringify({ steps: 30, cfgScale: 2.5 })],
	])("ignores %s", (_case, raw) => {
		localStorage.setItem(key, raw);

		expect(readImageFormOverrides("m")).toBeNull();
	});

	it("does not store an out-of-range value, keeping the last valid override", () => {
		writeImageFormOverrides("m", { steps: 30, cfgScale: 2.5, sampler: "euler" });

		writeImageFormOverrides("m", { steps: 300, cfgScale: 2.5, sampler: "euler" });

		expect(readImageFormOverrides("m")?.steps).toBe(30);
	});
});
