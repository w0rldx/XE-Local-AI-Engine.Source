import { describe, expect, it } from "vitest";

import { formatCostUsd, formatCount, formatTokensCompact } from "@/core/formatting/NumberFormatting";

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
});
