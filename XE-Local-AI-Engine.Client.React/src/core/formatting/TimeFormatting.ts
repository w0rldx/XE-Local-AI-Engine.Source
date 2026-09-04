// Shared time formatters. They live in core because the scheduler's run history and the integrations execution
// tables render the identical thing — a stored epoch-millis instant and a stored millisecond duration — and neither
// feature owns the concept. Both render an absent or unusable value as a dash rather than the literal "Invalid Date"
// a bare toLocaleString() would print into a table row.

/** Formats an epoch-millis instant in the viewer's locale, or a dash when absent or unusable. */
export function formatTimestamp(value: number | null): string {
	if (value === null) {
		return "—";
	}
	const date = new Date(value);
	return Number.isNaN(date.getTime()) ? "—" : date.toLocaleString();
}

/** Formats a millisecond duration as a compact seconds string, or a dash when absent. */
export function formatDurationSeconds(durationMs: number | null): string {
	return durationMs === null ? "—" : `${(durationMs / 1000).toFixed(1)}s`;
}
