import { describe, expect, it } from "vitest";

import { toSpeakableText } from "@/features/voice/SpeakableText";

describe("toSpeakableText", () => {
	it("keeps the inner text of bold markers", () => {
		expect(toSpeakableText("**word**")).toBe("word");
	});

	it("keeps the inner text of italic markers", () => {
		expect(toSpeakableText("*word*")).toBe("word");
	});

	it("keeps the inner text of an inline code span", () => {
		expect(toSpeakableText("`code span`")).toBe("code span");
	});

	it("keeps only the link text, dropping the URL", () => {
		expect(toSpeakableText("[text](https://example.com)")).toBe("text");
	});

	it("drops images entirely, including alt text", () => {
		expect(toSpeakableText("![alt text](https://example.com/pic.png)")).toBe("");
	});

	it("keeps heading text without the `#` markers", () => {
		expect(toSpeakableText("# Heading")).toBe("Heading");
	});

	it("keeps list item text without the bullet marker", () => {
		expect(toSpeakableText("- item one\n- item two")).toBe("item one item two");
	});

	it("keeps blockquote text without the `>` marker", () => {
		expect(toSpeakableText("> quoted text")).toBe("quoted text");
	});

	it("keeps table cell text, dropping pipe/dash syntax", () => {
		const table = "| Name | Age |\n| --- | --- |\n| Ada | 30 |";
		const result = toSpeakableText(table);
		expect(result).toContain("Name");
		expect(result).toContain("Age");
		expect(result).toContain("Ada");
		expect(result).toContain("30");
		expect(result).not.toContain("|");
		expect(result).not.toContain("---");
	});

	it("omits fenced code block content entirely", () => {
		const withCode = "Before.\n\n```js\nconst x = 1;\nconsole.log(x);\n```\n\nAfter.";
		const result = toSpeakableText(withCode);
		expect(result).not.toContain("const x");
		expect(result).not.toContain("console.log");
		expect(result).toBe("Before. After.");
	});

	it("passes German umlauts through untouched", () => {
		expect(toSpeakableText("**Größe** und Straße")).toBe("Größe und Straße");
	});

	it("returns an empty string when nothing is left to speak", () => {
		expect(toSpeakableText("```\nconst x = 1;\n```")).toBe("");
	});

	it("collapses multiple blank lines/whitespace to single spaces and trims", () => {
		expect(toSpeakableText("  Hello   world  \n\n\n  again  ")).toBe("Hello world again");
	});

	it("keeps autolinks/bare URLs as-is", () => {
		expect(toSpeakableText("Visit https://example.com for more.")).toBe("Visit https://example.com for more.");
	});

	it("keeps strikethrough inner text", () => {
		expect(toSpeakableText("~~deleted~~ text")).toBe("deleted text");
	});
});
