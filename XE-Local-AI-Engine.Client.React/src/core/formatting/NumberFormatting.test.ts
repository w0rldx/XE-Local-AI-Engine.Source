import i18next from "i18next";
import { afterEach, describe, expect, it } from "vitest";

import { formatCostUsd, formatCount, formatNumber, formatTokensCompact } from "@/core/formatting/NumberFormatting";

describe("NumberFormatting", () => {
	it("formatTokensCompact abbreviates large counts", () => {
		expect(formatTokensCompact(999)).toBe("999");
		expect(formatTokensCompact(1_500_000)).toMatch(/1\.5M/);
	});

	it("formatCount groups thousands", () => {
		// Grouping separator is locale-dependent; assert the digits survive and a separator is present.
		expect(formatCount(1234567)).toMatch(/1\D?234\D?567/);
	});

	it("formatCostUsd renders an em-dash for free/unpriced and a currency value otherwise", () => {
		// Zero / non-positive = free/local/unpriced -> em-dash, not "$0.00".
		expect(formatCostUsd(0)).toBe("—");
		expect(formatCostUsd(-1)).toBe("—");
		// A positive cost renders as a USD currency value (symbol + the digits, locale-formatted).
		expect(formatCostUsd(12.5)).toMatch(/\$?12[.,]50/);
		// A priced-but-tiny value still shows a currency value (distinct from the free em-dash).
		expect(formatCostUsd(0.004)).toMatch(/0[.,]00/);
	});

	describe("UI language", () => {
		afterEach(async () => {
			await i18next.changeLanguage("en");
		});

		// The formatters were module constants on the browser locale, so a German UI in an English browser kept English
		// separators, and a language switch changed nothing until a reload.
		it("formats in the active UI language and follows a switch without a reload", async () => {
			await i18next.changeLanguage("en");
			expect(formatCount(1234567)).toBe("1,234,567");
			expect(formatTokensCompact(1_500_000)).toBe("1.5M");
			expect(formatCostUsd(12.5)).toBe("$12.50");

			await i18next.changeLanguage("de");

			expect(formatCount(1234567)).toBe("1.234.567");
			expect(formatTokensCompact(1_500_000)).toMatch(/^1,5\sMio\.$/);
			expect(formatCostUsd(12.5)).toMatch(/^12,50\s\$$/);
		});

		it("formatNumber applies its options in the UI language", async () => {
			await i18next.changeLanguage("en");
			expect(formatNumber(1.5, { minimumFractionDigits: 2 })).toBe("1.50");
			expect(formatNumber(1.5, { maximumFractionDigits: 0 })).toBe("2");

			await i18next.changeLanguage("de");

			expect(formatNumber(1.5, { minimumFractionDigits: 2 })).toBe("1,50");
			expect(formatNumber(0.125, { style: "percent", maximumFractionDigits: 1 })).toMatch(/^12,5\s%$/);
		});

		it("falls back to the environment default when the stored language tag is malformed", async () => {
			await i18next.changeLanguage("en_US");

			// The premise, pinned: without the guard this tag throws inside Intl.
			expect(() => new Intl.NumberFormat("en_US")).toThrow(RangeError);
			expect(formatCount(1234567)).toMatch(/^1\D?234\D?567$/);
			expect(formatNumber(1.5, { minimumFractionDigits: 2 })).toMatch(/^1\D50$/);
		});
	});
});
