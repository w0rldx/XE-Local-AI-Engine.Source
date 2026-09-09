// The definition editor's pure cell helpers: what a picker's choice does to the stored document, and how the two
// free-text cells cross between the input's string and the scalar JSON the wire carries.

/**
 * The declared set after the picker changed: a capability that was already there keeps the reason its author wrote, and
 * a newly picked one starts empty so the operator is the one who says why. Answers `null` for an empty set, which is
 * how the wire says "declares nothing" — an empty object would be a document saying something it does not mean.
 */
export function withCapabilities(
	current: { readonly [key: string]: string } | null | undefined,
	effects: readonly string[],
): { readonly [key: string]: string } | null {
	if (effects.length === 0) {
		return null;
	}
	return Object.fromEntries(effects.map((effect) => [effect, current?.[effect] ?? ""]));
}

/** Mantine's NumberInput answers "" for an emptied field; the wire wants `null` there, not `NaN` and not `0`. */
export function toOptionalNumber(value: string | number): number | null {
	if (value === "" || value === null) {
		return null;
	}
	const parsed = typeof value === "number" ? value : Number.parseInt(value, 10);
	return Number.isFinite(parsed) ? parsed : null;
}

/** The stored scalar rendered for the text cell. A string shows as itself; everything else as its JSON text. */
export function readValue(value: unknown): string {
	if (value === undefined || value === null) {
		return "";
	}
	return typeof value === "string" ? value : JSON.stringify(value);
}

/**
 * The text cell back into the scalar JSON the wire carries. `true`, `42` and `null` become themselves — the server
 * compares by JSON kind, so storing them as text makes the edge dead with nothing logged — and anything else stays the
 * string it was typed as, because a decision token is a string and quoting it would be noise.
 *
 * ponytail: a stored STRING that looks like a number turns into a number if the operator edits that one cell. The
 * lossless alternative is showing every string quoted, which makes the ordinary case (`Approve`) read as `"Approve"`.
 */
export function parseConditionValue(text: string): unknown {
	try {
		const parsed: unknown = JSON.parse(text);
		return parsed === null || typeof parsed === "boolean" || typeof parsed === "number" ? parsed : text;
	} catch {
		return text;
	}
}
