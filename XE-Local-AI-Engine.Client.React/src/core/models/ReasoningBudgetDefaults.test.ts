import { describe, expect, it } from "vitest";

import { effectiveReasoningBudget, reasoningBudgetDefaults } from "@/core/models/ReasoningBudgetDefaults";

describe("effectiveReasoningBudget", () => {
	it("uses the stored ladder entry when the node has one", () => {
		expect(effectiveReasoningBudget({ reasoningBudgetHighTokens: 4096 }, "high")).toBe(4096);
	});

	it("falls back to the shipped default for an unset entry or no settings", () => {
		expect(effectiveReasoningBudget({ reasoningBudgetLowTokens: null }, "low")).toBe(2048);
		expect(effectiveReasoningBudget(undefined, "minimal")).toBe(reasoningBudgetDefaults.minimal);
		expect(reasoningBudgetDefaults).toEqual({ minimal: 1024, low: 2048, medium: 8192, high: 24576 });
	});
});
