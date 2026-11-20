// The shared byte formatters. The behavior worth pinning is the GiB decimal: it follows the UI language ("3,6 GiB" in
// a German session) while English output stays exactly what `toFixed(1)` used to print, which many feature tests
// assert as literal strings.

import i18next from "i18next";
import { afterEach, describe, expect, it } from "vitest";

import { formatBytes, formatBytesAsGb, formatOneDecimal, humanizeBytes } from "@/core/formatting/BytesFormatting";

const gib = 1024 ** 3;

afterEach(async () => {
	await i18next.changeLanguage("en");
});

describe("BytesFormatting", () => {
	it("prints English sizes exactly as before", async () => {
		await i18next.changeLanguage("en");

		expect(formatBytesAsGb(null)).toBe("—");
		expect(formatBytesAsGb(3.6 * gib)).toBe("3.6 GiB");
		expect(formatBytesAsGb(1234.5 * gib)).toBe("1234.5 GiB");
		// A tie rounds up, as toFixed did.
		expect(humanizeBytes(1.25 * gib)).toBe("1.3 GiB");
		expect(humanizeBytes(324 * 1_048_576)).toBe("324 MiB");
		expect(humanizeBytes(2048)).toBe("2 KiB");
		expect(humanizeBytes(12)).toBe("12 B");
	});

	it("uses the German decimal comma and no grouping in a German session", async () => {
		await i18next.changeLanguage("de");

		expect(formatBytesAsGb(3.6 * gib)).toBe("3,6 GiB");
		expect(formatBytesAsGb(1234.5 * gib)).toBe("1234,5 GiB");
		expect(humanizeBytes(1.2 * gib)).toBe("1,2 GiB");
		// Whole-number units read the same in every language.
		expect(humanizeBytes(324 * 1_048_576)).toBe("324 MiB");
	});

	it("follows a language switch at runtime", async () => {
		await i18next.changeLanguage("en");
		expect(formatBytesAsGb(3.6 * gib)).toBe("3.6 GiB");

		await i18next.changeLanguage("de");
		expect(formatBytesAsGb(3.6 * gib)).toBe("3,6 GiB");

		await i18next.changeLanguage("en");
		expect(formatBytesAsGb(3.6 * gib)).toBe("3.6 GiB");
	});

	it("formatBytes picks the largest unit, whole bytes below a KiB and one decimal above", async () => {
		await i18next.changeLanguage("en");

		expect(formatBytes(0)).toBe("0 B");
		expect(formatBytes(-5)).toBe("0 B");
		expect(formatBytes(Number.NaN)).toBe("0 B");
		expect(formatBytes(512)).toBe("512 B");
		expect(formatBytes(1023)).toBe("1023 B");
		expect(formatBytes(1024)).toBe("1.0 KiB");
		expect(formatBytes(1536)).toBe("1.5 KiB");
		expect(formatBytes(1_572_864)).toBe("1.5 MiB");
		expect(formatBytes(7_500_000_000)).toBe("7.0 GiB");
		expect(formatBytes(1.5 * 1024 ** 4)).toBe("1.5 TiB");
		// TiB is the top unit, so a petabyte still reads in TiB, ungrouped.
		expect(formatBytes(2048 * 1024 ** 4)).toBe("2048.0 TiB");
	});

	it("formatBytes keeps a decimal where humanizeBytes rounds to whole KiB and MiB", () => {
		expect(formatBytes(1536)).toBe("1.5 KiB");
		expect(humanizeBytes(1536)).toBe("2 KiB");
		expect(formatBytes(1_572_864)).toBe("1.5 MiB");
		expect(humanizeBytes(1_572_864)).toBe("2 MiB");
	});

	it("formatBytes and formatOneDecimal use the German decimal comma in a German session", async () => {
		await i18next.changeLanguage("de");

		expect(formatBytes(1536)).toBe("1,5 KiB");
		expect(formatBytes(1.25 * gib)).toBe("1,3 GiB");
		// Whole bytes carry no separator in any language.
		expect(formatBytes(512)).toBe("512 B");
		expect(formatOneDecimal(0.625)).toBe("0,6");
		expect(formatOneDecimal(1234.5)).toBe("1234,5");
	});

	it("falls back to the environment default when the stored language tag is malformed", async () => {
		await i18next.changeLanguage("en_US");

		expect(() => (1).toLocaleString("en_US")).toThrow(RangeError);
		expect(formatBytesAsGb(3.6 * gib)).toMatch(/^3[.,]6 GiB$/);
	});
});
