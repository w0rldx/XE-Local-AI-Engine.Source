// Shared time formatters. They live in core because every feature that renders a stored instant or a stored
// millisecond duration renders the identical thing, and none of them owns the concept. They render an absent or
// unusable value as a dash rather than the literal "Invalid Date" a bare toLocaleString() would print into a table row.

import i18next from "i18next";

/**
 * Formats an instant in the ACTIVE UI LANGUAGE, or a dash when absent or unusable.
 *
 * The value is epoch millis or an ISO string, because the wire carries both and a second helper would be a second
 * place for the language rule to drift out of. `undefined` counts as absent alongside `null`: most generated
 * timestamp fields are optional (`updatedAtUtc?: number`), and the alternative is a `?? null` at every call site.
 *
 * The language, not the browser locale: a session switched to German rendered German labels next to US-ordered dates,
 * because a bare `toLocaleString()` reads the machine's regional setting and knows nothing about i18next. Read here
 * rather than threaded through every call site, which is also why `core` may import i18next — the same reason
 * `ApiErrorMessage` and `Toast` do. An uninitialised i18next answers `undefined`, which is exactly the argument that
 * means "the environment's default", so a test or an early render behaves as it did before.
 *
 * `language` (what was REQUESTED) and not `resolvedLanguage` (what has a bundle): the locales are lazy chunks, so a
 * switch to German leaves `resolvedLanguage` on the English fallback until the chunk lands — and `addResourceBundle`
 * does not recompute it, so it can stay there. `toLocaleString` needs no bundle, only the tag, so the requested
 * language is both the honest answer and the one available first.
 *
 * `options` is the same `Intl.DateTimeFormatOptions` a bare `toLocaleString` takes, for the sites that render a
 * PART of the instant — a day label, a catalog release date. Naming any date field suppresses the clock defaults
 * exactly as `toLocaleDateString` would, so a site that moves here keeps its rendered output character for
 * character; passing the options in is what stops those sites needing a third formatter of their own.
 */
export function formatTimestamp(value: number | string | null | undefined, options?: Intl.DateTimeFormatOptions): string {
	return formatIn(value, (date, language) => date.toLocaleString(language, options));
}

/**
 * Formats the CLOCK PART of an instant in the active UI language, or a dash when absent or unusable.
 *
 * Same rule, same absent handling and the same optional `options` as {@link formatTimestamp}; the event feeds want
 * the time alone because every row shares the day, and the chat bubble wants it narrowed further to hours and
 * minutes.
 */
export function formatTime(value: number | string | null | undefined, options?: Intl.DateTimeFormatOptions): string {
	return formatIn(value, (date, language) => date.toLocaleTimeString(language, options));
}

// The guard and the language lookup live here so the two renderers cannot drift apart.
function formatIn(
	value: number | string | null | undefined,
	render: (date: Date, language: string | undefined) => string,
): string {
	if (value === null || value === undefined) {
		return "—";
	}
	const date = new Date(value);
	if (Number.isNaN(date.getTime())) {
		return "—";
	}
	try {
		return render(date, i18next.language ?? i18next.resolvedLanguage);
	} catch {
		// A malformed stored tag — `en_US` left in `i18nextLng` by hand — is a RangeError, and it would throw once per
		// table ROW. The environment's own default is a worse date, not a broken page.
		return render(date, undefined);
	}
}

/** Formats a millisecond duration as a compact seconds string, or a dash when absent. */
export function formatDurationSeconds(durationMs: number | null): string {
	return durationMs === null ? "—" : `${(durationMs / 1000).toFixed(1)}s`;
}
