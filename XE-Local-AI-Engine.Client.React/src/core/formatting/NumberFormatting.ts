// Shared count and cost formatters. They live in core because the usage dashboard and the Home usage card render the
// same totals and must agree on how a token count, a run count and an estimated cost read.

import i18next from "i18next";

interface NumberFormats {
	// Compact token formatter (e.g. 1234 → "1.2K", 1_500_000 → "1.5M"). Large token counts dominate usage views, so the
	// default display is compact; the full value is available via {@link formatCount} for tooltips/aria.
	readonly compact: Intl.NumberFormat;
	// Grouped full-precision formatter (thousands separators) for exact counts (run counts, tooltip values).
	readonly full: Intl.NumberFormat;
	// USD currency formatter for the estimated-cost figures (server rates are USD per the wire `currency` field).
	readonly usd: Intl.NumberFormat;
}

function createFormats(language: string | undefined): NumberFormats {
	return {
		compact: new Intl.NumberFormat(language, { notation: "compact", maximumFractionDigits: 1 }),
		full: new Intl.NumberFormat(language),
		usd: new Intl.NumberFormat(language, { style: "currency", currency: "USD", maximumFractionDigits: 2 }),
	};
}

let cached: { readonly language: string | undefined; readonly formats: NumberFormats } | undefined;

// The formatters follow the REQUESTED UI language, for the reason `formatTimestamp` gives in TimeFormatting.ts, and are
// rebuilt only when it changes, so a language switch re-renders in the new format without a reload.
function formats(): NumberFormats {
	const language = i18next.language ?? i18next.resolvedLanguage;
	if (cached === undefined || cached.language !== language) {
		let built: NumberFormats;
		try {
			built = createFormats(language);
		} catch {
			// A malformed stored tag (`en_US`) is a RangeError; the environment default is the honest fallback.
			built = createFormats(undefined);
		}
		cached = { language, formats: built };
	}
	return cached.formats;
}

export function formatTokensCompact(value: number): string {
	return formats().compact.format(value);
}

export function formatCount(value: number): string {
	return formats().full.format(value);
}

// Formats a server-computed estimated cost (USD) for display. A zero (or non-positive / non-finite) cost means the
// usage was free/local/unpriced and renders as an em-dash rather than "$0.00", so a genuinely-free row reads as free
// while a priced-but-tiny row still shows a currency value. Never recomputes cost — it only formats the wire value.
export function formatCostUsd(value: number): string {
	return value > 0 ? formats().usd.format(value) : "—";
}
