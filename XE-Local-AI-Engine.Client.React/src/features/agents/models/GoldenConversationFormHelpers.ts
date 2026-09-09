import type { GoldenTurn } from "@/features/agents/models/GoldenConversationModels";

/** Parse a newline-separated textarea into trimmed non-empty lines (used for input turns + phrase lists). */
export function toLines(value: string): string[] {
	return value
		.split("\n")
		.map((line) => line.trim())
		.filter((line) => line.length > 0);
}

// A free-text input-turn line authored as "role: text" → { role, text }. A line without a colon is treated as a
// user turn (the common case), so an operator can type plain prompts. Defensive against an empty role.
export function parseTurnLine(line: string): GoldenTurn {
	const separator = line.indexOf(":");
	if (separator < 0) {
		return { role: "user", text: line };
	}
	const role = line.slice(0, separator).trim();
	const text = line.slice(separator + 1).trim();
	return { role: role.length > 0 ? role : "user", text };
}

export function truncate(value: string, max: number): string {
	return value.length > max ? `${value.slice(0, max)}…` : value;
}

// Consolidated form state for the golden-case add form. The text fields plus the on-submit validation message live in
// one object so a logical update never fans out into separate renders (was 6 useState calls).
export interface GoldenFormState {
	title: string;
	turnsText: string;
	requiredText: string;
	forbiddenText: string;
	rubric: string;
	validationError: string | null;
}

export type GoldenFormAction =
	| { type: "setField"; field: "title" | "turnsText" | "requiredText" | "forbiddenText" | "rubric"; value: string }
	| { type: "setValidationError"; value: string | null };
