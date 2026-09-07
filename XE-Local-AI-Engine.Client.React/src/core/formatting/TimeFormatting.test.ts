// The shared instant/duration formatters. The one behaviour worth pinning is whose locale a timestamp is rendered in:
// a session switched to German used to render German labels beside US-ordered dates, because `toLocaleString()` reads
// the machine's regional setting and knows nothing about the language the operator chose.
//
// The assertions are on the SHAPE of the date (dots versus slashes), not on a literal string: the machine's time zone
// shifts the clock part, and pinning that would make the suite pass only where it was written.

import i18next from "i18next";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import { formatDurationSeconds, formatTime, formatTimestamp } from "@/core/formatting/TimeFormatting";

const instant = Date.UTC(2025, 2, 12, 13, 0, 0);

// File-level rather than inside the first describe: every block below reads the same i18next singleton, and scoping
// the init to one of them would leave the others depending on the order the runner happens to pick.
beforeAll(async () => {
	await i18next.init({ lng: "en-US", resources: { "en-US": { translation: {} }, de: { translation: {} } } });
});

afterAll(async () => {
	await i18next.changeLanguage("en-US");
});

describe("formatTimestamp", () => {
	it("renders a dash for an absent or unusable instant", () => {
		expect(formatTimestamp(null)).toBe("—");
		expect(formatTimestamp(undefined)).toBe("—");
		expect(formatTimestamp(Number.NaN)).toBe("—");
		expect(formatTimestamp("not-a-date")).toBe("—");
	});

	// Half the wire carries an ISO string and half epoch millis, so the same helper takes both rather than the
	// language rule living in two places.
	it("accepts an ISO string as well as epoch millis", async () => {
		await i18next.changeLanguage("de");
		expect(formatTimestamp(new Date(instant).toISOString())).toBe(formatTimestamp(instant));
		expect(formatTimestamp(new Date(instant).toISOString())).toMatch(/^\d{1,2}\.\d{1,2}\.\d{4}/);
	});

	it("falls back to the environment default when the stored language tag is malformed", async () => {
		await i18next.changeLanguage("en_US");

		// The premise, pinned: without the guard this exact call is what throws once per table row.
		expect(i18next.language).toBe("en_US");
		expect(() => new Date(instant).toLocaleString("en_US")).toThrow(RangeError);
		expect(formatTimestamp(instant)).toMatch(/^\d{1,2}\/\d{1,2}\/\d{4}/);
	});

	it("formats in the active UI language, not the machine's regional format", async () => {
		await i18next.changeLanguage("en-US");
		expect(formatTimestamp(instant)).toMatch(/^\d{1,2}\/\d{1,2}\/\d{4}/);

		await i18next.changeLanguage("de");

		expect(i18next.language).toBe("de");
		expect(formatTimestamp(instant)).toMatch(/^\d{1,2}\.\d{1,2}\.\d{4}/);
	});

	// The state every language switch passes through: the locales are lazy chunks, so German is REQUESTED while
	// i18next has resolved to the English fallback — and `addResourceBundle` does not recompute that, so it can stay
	// there. Reading `resolvedLanguage` rendered US-ordered dates beside German labels, which is the bug this whole
	// module exists for.
	it("formats in the requested language before its bundle has arrived", async () => {
		await i18next.changeLanguage("en-US");
		i18next.removeResourceBundle("de", "translation");
		await i18next.changeLanguage("de");

		// The premise, pinned: without it the assertion below would pass for the wrong reason.
		expect(i18next.language).toBe("de");
		expect(i18next.resolvedLanguage).not.toBe("de");
		expect(formatTimestamp(instant)).toMatch(/^\d{1,2}\.\d{1,2}\.\d{4}/);

		i18next.addResourceBundle("de", "translation", {});
	});
});

// The four sites that pass explicit options (the chat clock, the conversation-list day, the model-fit release date
// and the usage dashboard's day label) moved onto these helpers rather than growing a third formatter. What has to
// hold is that the options decide the FIELDS while the language still decides the order, and that a partial field
// set is not silently padded back out to a full date-and-time by `toLocaleString`'s defaults.
//
// The output is asserted against the very `toLocaleDateString`/`toLocaleTimeString` call each site used to make,
// which is the parity that mattered, and against the SHAPE across a language switch — not a literal month name or
// AM/PM separator, both of which move between ICU versions.
const dayOptions: Intl.DateTimeFormatOptions = { year: "numeric", month: "short", day: "numeric", timeZone: "UTC" };

describe("the options parameter", () => {
	it("renders exactly the named fields, matching the toLocaleDateString call each site replaced", async () => {
		await i18next.changeLanguage("en-US");

		expect(formatTimestamp(instant, dayOptions)).toBe(new Date(instant).toLocaleDateString("en-US", dayOptions));

		// The premise, pinned: `toLocaleString` pads its options out to a date AND a clock only when they name no
		// date field at all, so a migrated site keeps its date-only output instead of growing a time of day.
		expect(formatTimestamp(instant, dayOptions)).not.toMatch(/\d:\d/);
	});

	it("still formats in the active UI language, not the machine's", async () => {
		await i18next.changeLanguage("en-US");

		// English leads with the month, German with the day — the ordering bug this module exists for, under options.
		expect(formatTimestamp(instant, dayOptions)).toMatch(/^\D/);
		expect(formatTimestamp(instant, dayOptions)).toMatch(/\b12\b/);

		await i18next.changeLanguage("de");

		expect(i18next.language).toBe("de");
		expect(formatTimestamp(instant, dayOptions)).toMatch(/^12\./);
	});

	// The language fallback re-renders with the LANGUAGE dropped, so an options object that is invalid on its own
	// throws a second time from inside the catch — and the helper promises a dash, not a thrown table row.
	it("answers the dash rather than throwing when the options themselves are invalid", async () => {
		await i18next.changeLanguage("en-US");
		// `dateStyle` beside a component field is a spec TypeError, whatever the locale.
		const invalidOptions = { dateStyle: "full", year: "numeric" } as Intl.DateTimeFormatOptions;

		// The premise, pinned: this exact call is what throws, in both the first attempt and the fallback.
		expect(() => new Date(instant).toLocaleString("en-US", invalidOptions)).toThrow(TypeError);
		expect(() => new Date(instant).toLocaleString(undefined, invalidOptions)).toThrow(TypeError);

		expect(formatTimestamp(instant, invalidOptions)).toBe("—");
		expect(formatTime(instant, invalidOptions)).toBe("—");
	});

	it("still renders through the fallback when the language is malformed but the options are fine", async () => {
		await i18next.changeLanguage("en_US");

		expect(i18next.language).toBe("en_US");
		expect(formatTimestamp(instant, dayOptions)).not.toBe("—");
		expect(formatTimestamp(instant, dayOptions)).toBe(new Date(instant).toLocaleDateString(undefined, dayOptions));
	});

	it("narrows the clock the same way, and answers the dash for an absent value with options in hand", async () => {
		await i18next.changeLanguage("en-US");
		const clockOptions: Intl.DateTimeFormatOptions = { hour: "2-digit", minute: "2-digit", timeZone: "UTC" };

		expect(formatTime(instant, clockOptions)).toBe(new Date(instant).toLocaleTimeString("en-US", clockOptions));
		expect(formatTime(instant, clockOptions)).toMatch(/^01:00/);

		// The absent guard runs before the renderer, so options change nothing about it.
		expect(formatTime(null, clockOptions)).toBe("—");
		expect(formatTimestamp(undefined, dayOptions)).toBe("—");
	});
});

describe("formatTime", () => {
	// The event feeds render the clock alone, and they used to coalesce an absent instant to epoch zero — which reads
	// as a real time of day, unlike the dash.
	it("renders the clock part in the active UI language, and a dash when absent", async () => {
		await i18next.changeLanguage("en-US");
		expect(formatTime(instant)).toBe(new Date(instant).toLocaleTimeString("en-US"));
		expect(formatTime(new Date(instant).toISOString())).toBe(formatTime(instant));
		expect(formatTime(null)).toBe("—");
		expect(formatTime(undefined)).toBe("—");
	});
});

describe("formatDurationSeconds", () => {
	it("renders a dash for an absent duration and one decimal otherwise", () => {
		expect(formatDurationSeconds(null)).toBe("—");
		expect(formatDurationSeconds(1500)).toBe("1.5s");
	});
});
