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
 */
export function formatTimestamp(value: number | null): string {
	if (value === null) {
		return "—";
	}
	const date = new Date(value);
	return Number.isNaN(date.getTime()) ? "—" : date.toLocaleString(i18next.resolvedLanguage ?? i18next.language);
}

/** Formats a millisecond duration as a compact seconds string, or a dash when absent. */
export function formatDurationSeconds(durationMs: number | null): string {
	return durationMs === null ? "—" : `${(durationMs / 1000).toFixed(1)}s`;
}
