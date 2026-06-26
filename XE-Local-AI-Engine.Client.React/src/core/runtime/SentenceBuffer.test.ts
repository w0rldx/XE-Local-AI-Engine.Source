import { describe, expect, it } from "vitest";

import { SentenceBuffer, sanitizeForSpeech } from "./SentenceBuffer";

describe("SentenceBuffer flushing", () => {
	it("buffers partial tokens and flushes only on a sentence boundary (not per token)", () => {
		const buffer = new SentenceBuffer();

		expect(buffer.push("Hello")).toEqual([]);
		expect(buffer.push(" there")).toEqual([]);
		expect(buffer.push(" world.")).toEqual(["Hello there world."]);
	});

	it("flushes multiple complete sentences in one push", () => {
		const buffer = new SentenceBuffer();

		expect(buffer.push("First one. Second one! Third?")).toEqual(["First one.", "Second one!", "Third?"]);
	});

	it("treats a newline as a flush boundary", () => {
		const buffer = new SentenceBuffer();

		expect(buffer.push("Line one\nrest")).toEqual(["Line one"]);
	});

	it("flushes at the 512-char cap when no sentence boundary has arrived", () => {
		const buffer = new SentenceBuffer();
		const longRun = "a".repeat(600);

		const flushed = buffer.push(longRun);

		expect(flushed).toHaveLength(1);
		expect(flushed[0]).toHaveLength(512);
		expect(buffer.flush()).toBe("a".repeat(88));
	});

	it("emits the trailing remainder on flush()", () => {
		const buffer = new SentenceBuffer();
		buffer.push("No terminator here");

		expect(buffer.flush()).toBe("No terminator here");
		expect(buffer.flush()).toBeUndefined();
	});
});

describe("SentenceBuffer sanitization (invariant 3.9)", () => {
	it("strips fenced code, inline code, bare URLs, and link syntax while preserving prose", () => {
		const buffer = new SentenceBuffer();
		const input = "See ```const x = 1;``` and `inline` then visit https://example.com or [the docs](http://docs.test) now.";

		const [sentence] = buffer.push(input);

		expect(sentence).toBeDefined();
		expect(sentence).not.toContain("`");
		expect(sentence).not.toContain("http");
		expect(sentence).not.toContain("const x");
		// Link text and surrounding prose survive.
		expect(sentence).toContain("the docs");
		expect(sentence).toContain("See");
		expect(sentence).toContain("now.");
	});

	it("preserves bold and italic markers", () => {
		expect(sanitizeForSpeech("This is **bold** and _italic_ text.")).toBe("This is **bold** and _italic_ text.");
	});

	it("drops a segment that is purely code", () => {
		const buffer = new SentenceBuffer();

		expect(buffer.push("`onlycode`\n")).toEqual([]);
	});
});
