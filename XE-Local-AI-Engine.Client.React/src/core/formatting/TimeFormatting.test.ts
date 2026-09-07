// The shared instant/duration formatters. The one behaviour worth pinning is whose locale a timestamp is rendered in:
// a session switched to German used to render German labels beside US-ordered dates, because `toLocaleString()` reads
// the machine's regional setting and knows nothing about the language the operator chose.
//
// The assertions are on the SHAPE of the date (dots versus slashes), not on a literal string: the machine's time zone
// shifts the clock part, and pinning that would make the suite pass only where it was written.

import i18next from "i18next";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import { formatDurationSeconds, formatTimestamp } from "@/core/formatting/TimeFormatting";

const instant = Date.UTC(2025, 2, 12, 13, 0, 0);

describe("formatTimestamp", () => {
	beforeAll(async () => {
		await i18next.init({ lng: "en-US", resources: { "en-US": { translation: {} }, de: { translation: {} } } });
	});

	afterAll(async () => {
		await i18next.changeLanguage("en-US");
	});

	it("renders a dash for an absent or unusable instant", () => {
		expect(formatTimestamp(null)).toBe("—");
		expect(formatTimestamp(Number.NaN)).toBe("—");
	});

	it("falls back to the environment default when the stored language tag is malformed", async () => {
		await i18next.changeLanguage("en_US");

		// The premise, pinned: without the guard this exact call is what throws once per table row.
		expect(i18next.language).toBe("en_US");
		expect(() => new Date(instant).toLocaleString("en_US")).toThrow(RangeError);
		expect(formatTimestamp(instant)).toMatch(/^\d{1,2}\/\d{1,2}\/\d{4}/);
	});

	it("formats in the active UI language, not the machine's regional format", async () => {
		expect(formatTimestamp(instant)).toMatch(/^\d{1,2}\/\d{1,2}\/\d{4}/);

		await i18next.changeLanguage("de");

		expect(i18next.language).toBe("de");
		expect(formatTimestamp(instant)).toMatch(/^\d{1,2}\.\d{1,2}\.\d{4}/);
	});
});

describe("formatDurationSeconds", () => {
	it("renders a dash for an absent duration and one decimal otherwise", () => {
		expect(formatDurationSeconds(null)).toBe("—");
		expect(formatDurationSeconds(1500)).toBe("1.5s");
	});
});
