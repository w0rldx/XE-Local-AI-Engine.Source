// Shared count and cost formatters. They live in core because the usage dashboard and the Home usage card render the
// same totals and must agree on how a token count, a run count and an estimated cost read.

// Compact token formatter (e.g. 1234 → "1.2K", 1_500_000 → "1.5M"). Large token counts dominate usage views, so the
// default display is compact; the full value is available via {@link formatCount} for tooltips/aria.
const compactNumberFormat = new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 });
// Grouped full-precision formatter (thousands separators) for exact counts (run counts, tooltip values).
const fullNumberFormat = new Intl.NumberFormat();
// USD currency formatter for the estimated-cost figures (server rates are USD per the wire `currency` field).
const usdCurrencyFormat = new Intl.NumberFormat(undefined, { style: "currency", currency: "USD", maximumFractionDigits: 2 });

export function formatTokensCompact(value: number): string {
	return compactNumberFormat.format(value);
}

export function formatCount(value: number): string {
	return fullNumberFormat.format(value);
}

// Formats a server-computed estimated cost (USD) for display. A zero (or non-positive / non-finite) cost means the
// usage was free/local/unpriced and renders as an em-dash rather than "$0.00", so a genuinely-free row reads as free
// while a priced-but-tiny row still shows a currency value. Never recomputes cost — it only formats the wire value.
export function formatCostUsd(value: number): string {
	return value > 0 ? usdCurrencyFormat.format(value) : "—";
}
