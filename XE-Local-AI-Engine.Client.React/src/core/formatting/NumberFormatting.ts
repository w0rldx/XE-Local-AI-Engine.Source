// Shared number formatters. They live in core because every feature that renders a count, a score or a measured
// value renders it in the UI language, and the usage dashboard and the Home usage card must agree on how a token
// count, a run count and an estimated cost read.

import i18next from "i18next";

// Compact token formatter (e.g. 1234 → "1.2K", 1_500_000 → "1.5M"). Large token counts dominate usage views, so the
// default display is compact; the full value is available via {@link formatCount} for tooltips/aria.
const compact: Intl.NumberFormatOptions = { notation: "compact", maximumFractionDigits: 1 };
// USD currency formatter for the estimated-cost figures (server rates are USD per the wire `currency` field).
const usd: Intl.NumberFormatOptions = { style: "currency", currency: "USD", maximumFractionDigits: 2 };

let cached: { readonly language: string | undefined; readonly formats: Map<string, Intl.NumberFormat> } | undefined;

// The formatters follow the REQUESTED UI language, for the reason `formatTimestamp` gives in TimeFormatting.ts, and are
// rebuilt only when it changes, so a language switch re-renders in the new format without a reload. One formatter per
// option set: a stepper view formats dozens of values per render.
function formats(options: Intl.NumberFormatOptions | undefined): Intl.NumberFormat {
	const language = i18next.language ?? i18next.resolvedLanguage;
	if (cached === undefined || cached.language !== language) {
		cached = { language, formats: new Map() };
	}
	const key = JSON.stringify(options ?? {});
	let format = cached.formats.get(key);
	if (format === undefined) {
		try {
			format = new Intl.NumberFormat(language, options);
		} catch {
			// A malformed stored tag (`en_US`) is a RangeError; the environment default is the honest fallback.
			format = new Intl.NumberFormat(undefined, options);
		}
		cached.formats.set(key, format);
	}
	return format;
}

/** Formats a number in the UI language; `options` are the `Intl.NumberFormatOptions` a bare `toLocaleString` takes. */
export function formatNumber(value: number, options?: Intl.NumberFormatOptions): string {
	return formats(options).format(value);
}

export function formatTokensCompact(value: number): string {
	return formatNumber(value, compact);
}

// Grouped full-precision formatter (thousands separators) for exact counts (run counts, tooltip values).
export function formatCount(value: number): string {
	return formatNumber(value);
}

// Formats a server-computed estimated cost (USD) for display. A zero (or non-positive / non-finite) cost means the
// usage was free/local/unpriced and renders as an em-dash rather than "$0.00", so a genuinely-free row reads as free
// while a priced-but-tiny row still shows a currency value. Never recomputes cost — it only formats the wire value.
export function formatCostUsd(value: number): string {
	return value > 0 ? formatNumber(value, usd) : "—";
}
