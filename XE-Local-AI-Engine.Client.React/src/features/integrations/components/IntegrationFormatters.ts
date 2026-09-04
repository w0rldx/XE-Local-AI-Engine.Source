/**
 * Short form of a principal (or any GUID) for a table cell: the first 8 characters, with the full value carried in
 * the cell's tooltip/title. Rows for the same principal must be comparable at a glance, which is the whole point of
 * rendering the identity at all.
 */
export function shortPrincipalId(principalId: string): string {
	return principalId.slice(0, 8);
}

/**
 * Formats an epoch-millis timestamp for a table cell. A value the runtime cannot turn into a date renders as a dash
 * rather than the literal "Invalid Date" a bare toLocaleString() would print into the row.
 */
export function formatIntegrationTimestamp(value: number): string {
	const date = new Date(value);
	return Number.isNaN(date.getTime()) ? "—" : date.toLocaleString();
}

/**
 * Formats an optional epoch-millis timestamp. `null` is a legitimate value on an execution row — a run cancelled or
 * failed before it took the node's lease never started and never ended — so it reads as a dash, not as an error.
 */
export function formatIntegrationOptionalTimestamp(value: number | null): string {
	return value === null ? "—" : formatIntegrationTimestamp(value);
}

/**
 * Wall-clock duration of a run, in compact seconds. Both endpoints are required: a run that never started has no
 * duration to state, and inventing one from `receivedAtUtc` would report queue time as execution time.
 */
export function formatIntegrationDuration(startedAtUtc: number | null, endedAtUtc: number | null): string {
	if (startedAtUtc === null || endedAtUtc === null) {
		return "—";
	}
	return `${((endedAtUtc - startedAtUtc) / 1000).toFixed(1)}s`;
}
