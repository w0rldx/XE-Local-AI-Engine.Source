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
