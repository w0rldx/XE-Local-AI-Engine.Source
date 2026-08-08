import { afterEach, describe, expect, it } from "vitest";

import { buildErrorKey, DEDUP_WINDOW_MS, reset, shouldRecord } from "@/core/diagnostics/Dedup";

afterEach(() => reset());

describe("dedup gate", () => {
	it("records the same error key only once within the suppression window", () => {
		const key = buildErrorKey("Boom", "Error: Boom\n    at Component (App.tsx:10:5)");

		// Same logical error seen by boundary + window + console handlers at the same instant.
		expect(shouldRecord(key, 1_000)).toBe(true);
		expect(shouldRecord(key, 1_200)).toBe(false);
		expect(shouldRecord(key, 1_500)).toBe(false);
	});

	it("allows the key again once the window has elapsed", () => {
		const key = buildErrorKey("Boom", "Error: Boom\n    at Component (App.tsx:10:5)");

		expect(shouldRecord(key, 0)).toBe(true);
		expect(shouldRecord(key, DEDUP_WINDOW_MS)).toBe(true);
	});

	it("treats different top frames as distinct errors", () => {
		const a = buildErrorKey("Boom", "Error: Boom\n    at A (a.ts:1:1)");
		const b = buildErrorKey("Boom", "Error: Boom\n    at B (b.ts:2:2)");

		expect(a).not.toBe(b);
		expect(shouldRecord(a, 0)).toBe(true);
		expect(shouldRecord(b, 0)).toBe(true);
	});
});
