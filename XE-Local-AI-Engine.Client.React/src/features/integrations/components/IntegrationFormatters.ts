/**
 * Short form of a principal (or any GUID) for a table cell: the first 8 characters, with the full value carried in
 * the cell's tooltip/title. Rows for the same principal must be comparable at a glance, which is the whole point of
 * rendering the identity at all.
 */
export function shortPrincipalId(principalId: string): string {
	return principalId.slice(0, 8);
}
