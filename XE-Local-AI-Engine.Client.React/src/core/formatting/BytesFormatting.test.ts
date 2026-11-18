// The shared byte formatters. The behavior worth pinning is the GiB decimal: it follows the UI language ("3,6 GiB" in
// a German session) while English output stays exactly what `toFixed(1)` used to print, which many feature tests
// assert as literal strings.

import i18next from "i18next";
import { afterEach, describe, expect, it } from "vitest";

import { formatBytesAsGb, humanizeBytes } from "@/core/formatting/BytesFormatting";

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

	it("falls back to the environment default when the stored language tag is malformed", async () => {
		await i18next.changeLanguage("en_US");

		expect(() => (1).toLocaleString("en_US")).toThrow(RangeError);
		expect(formatBytesAsGb(3.6 * gib)).toMatch(/^3[.,]6 GiB$/);
	});
});
