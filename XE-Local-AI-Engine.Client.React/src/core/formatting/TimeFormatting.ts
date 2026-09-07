// Shared time formatters. They live in core because the scheduler's run history and the integrations execution
// tables render the identical thing — a stored epoch-millis instant and a stored millisecond duration — and neither
// feature owns the concept. Both render an absent or unusable value as a dash rather than the literal "Invalid Date"
// a bare toLocaleString() would print into a table row.

import i18next from "i18next";

/**
 * Formats an epoch-millis instant in the ACTIVE UI LANGUAGE, or a dash when absent or unusable.
 *
 * The language, not the browser locale: a session switched to German rendered German labels next to US-ordered dates,
 * because a bare `toLocaleString()` reads the machine's regional setting and knows nothing about i18next. Read here
 * rather than threaded through the fifteen call sites, which is also why `core` may import i18next — the same reason
 * `ApiErrorMessage` and `Toast` do. An uninitialised i18next answers `undefined`, which is exactly the argument that
 * means "the environment's default", so a test or an early render behaves as it did before.
 *
 * `language` (what was REQUESTED) and not `resolvedLanguage` (what has a bundle): the locales are lazy chunks, so a
 * switch to German leaves `resolvedLanguage` on the English fallback until the chunk lands — and `addResourceBundle`
 * does not recompute it, so it can stay there. `toLocaleString` needs no bundle, only the tag, so the requested
 * language is both the honest answer and the one available first.
 */
export function formatTimestamp(value: number | null): string {
	if (value === null) {
		return "—";
	}
	const date = new Date(value);
	if (Number.isNaN(date.getTime())) {
		return "—";
	}
	try {
		return date.toLocaleString(i18next.language ?? i18next.resolvedLanguage);
	} catch {
		// A malformed stored tag — `en_US` left in `i18nextLng` by hand — is a RangeError, and it would throw once per
		// table ROW. The environment's own default is a worse date, not a broken page.
		return date.toLocaleString();
	}
}

/** Formats a millisecond duration as a compact seconds string, or a dash when absent. */
export function formatDurationSeconds(durationMs: number | null): string {
	return durationMs === null ? "—" : `${(durationMs / 1000).toFixed(1)}s`;
}
