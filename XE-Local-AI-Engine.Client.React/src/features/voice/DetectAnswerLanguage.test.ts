import { describe, expect, it } from "vitest";

import { detectAnswerLanguage } from "@/features/voice/DetectAnswerLanguage";

describe("detectAnswerLanguage", () => {
	it("defaults to English for plain English prose", () => {
		expect(detectAnswerLanguage("The quick brown fox jumps over the lazy dog.")).toBe("en");
	});

	it("returns English for empty input", () => {
		expect(detectAnswerLanguage("")).toBe("en");
	});

	it("detects German from an umlaut", () => {
		expect(detectAnswerLanguage("Schön, dich zu sehen.")).toBe("de");
	});

	it("detects German from the sharp-s (ß)", () => {
		expect(detectAnswerLanguage("Die Straße ist groß.")).toBe("de");
	});

	it("detects German from multiple stopwords without diacritics", () => {
		expect(detectAnswerLanguage("Das ist nicht der richtige Weg und das weiss ich.")).toBe("de");
	});

	it("does not flip to German on a single ambiguous word", () => {
		// "die" appears once as an English word; below the stopword threshold and no diacritics → English.
		expect(detectAnswerLanguage("They will die without water.")).toBe("en");
	});
});
