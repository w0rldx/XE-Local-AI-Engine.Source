import type { XeLocalAiEngineClientEndpointsNodeSettingsV1NodeSettingsResponse as NodeSettingsResponse } from "@/core/api/generated";

// The efforts that carry a thinking budget on the node's ladder.
export type BudgetedReasoningEffort = "minimal" | "low" | "medium" | "high";

// What the node uses while a ladder entry is unset; mirrors ReasoningBudgets.Default (AI.Agent).
export const reasoningBudgetDefaults: Readonly<Record<BudgetedReasoningEffort, number>> = {
	minimal: 1024,
	low: 2048,
	medium: 8192,
	high: 24576,
};

export type ReasoningBudgetSettings = Pick<
	NodeSettingsResponse,
	"reasoningBudgetMinimalTokens" | "reasoningBudgetLowTokens" | "reasoningBudgetMediumTokens" | "reasoningBudgetHighTokens"
>;

/** The budget the node gives an effort: the stored ladder entry, else the shipped default. */
export function effectiveReasoningBudget(settings: ReasoningBudgetSettings | undefined, effort: BudgetedReasoningEffort): number {
	const stored = {
		minimal: settings?.reasoningBudgetMinimalTokens,
		low: settings?.reasoningBudgetLowTokens,
		medium: settings?.reasoningBudgetMediumTokens,
		high: settings?.reasoningBudgetHighTokens,
	}[effort];
	return stored ?? reasoningBudgetDefaults[effort];
}
