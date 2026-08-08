import { describe, expect, it } from "vitest";

import { evaluateComposerSize, toDisplayKb, utf8ByteLength } from "@/features/chat/models/ComposerSizeLimit";

// 1 KB limit keeps the fixtures small; the thresholds scale linearly.
const limitKb = 1;

function ascii(byteLength: number): string {
	return "a".repeat(byteLength);
}

describe("composer size measurement", () => {
	it("counts UTF-8 bytes, not code units", () => {
		// 1-byte, 2-byte, 3-byte and 4-byte (astral, 2 code units) code points.
		expect(utf8ByteLength("a")).toBe(1);
		expect(utf8ByteLength("é")).toBe(2);
		expect(utf8ByteLength("€")).toBe(3);
		expect(utf8ByteLength("😀")).toBe(4);
	});

	it("rounds the displayed KB up so a reported size is never at or below the limit", () => {
		expect(toDisplayKb(1024)).toBe(1);
		expect(toDisplayKb(1025)).toBe(2);
	});
});

describe("composer size threshold states", () => {
	it("stays silent while the draft is comfortably under the limit", () => {
		expect(evaluateComposerSize(ascii(500), limitKb)).toBeUndefined();
	});

	it("reports the size once the draft passes 80 percent of the limit", () => {
		const state = evaluateComposerSize(ascii(900), limitKb);

		expect(state).toEqual({ bytes: 900, limitBytes: 1024, overLimit: false });
	});

	it("treats a draft exactly at the limit as within it", () => {
		expect(evaluateComposerSize(ascii(1024), limitKb)?.overLimit).toBe(false);
	});

	it("flags a draft one byte over the limit", () => {
		expect(evaluateComposerSize(ascii(1025), limitKb)?.overLimit).toBe(true);
	});

	it("measures multi-byte text by its encoded size, not its character count", () => {
		// 400 three-byte characters = 1200 bytes: under the limit by character count, over it on the wire.
		const state = evaluateComposerSize("€".repeat(400), limitKb);

		expect(state).toEqual({ bytes: 1200, limitBytes: 1024, overLimit: true });
	});

	it("skips the pre-check entirely when the node limit is unknown", () => {
		expect(evaluateComposerSize(ascii(5000), undefined)).toBeUndefined();
	});
});
