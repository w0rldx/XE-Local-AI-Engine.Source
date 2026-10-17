import type { TFunction } from "i18next";

import { formatDurationSeconds } from "@/core/formatting/TimeFormatting";

/**
 * Short form of a principal (or any GUID) for a table cell: the first 8 characters, with the full value carried in
 * the cell's tooltip/title. Rows for the same principal must be comparable at a glance, which is the whole point of
 * rendering the identity at all.
 */
export function shortPrincipalId(principalId: string): string {
	return principalId.slice(0, 8);
}

/**
 * Wall-clock duration of a run, from the two instants an execution row stores rather than from a stored duration.
 * Both endpoints are required: a run that never started has no duration to state, and inventing one from
 * `receivedAtUtc` would report queue time as execution time.
 */
export function formatIntegrationDuration(startedAtUtc: number | null, endedAtUtc: number | null): string {
	if (startedAtUtc === null || endedAtUtc === null) {
		return "—";
	}
	return formatDurationSeconds(endedAtUtc - startedAtUtc);
}

/**
 * The operator-facing label of an execution failure category. A category this client does not know renders as the raw
 * wire value, so a newer server's category stays readable instead of disappearing.
 */
export function formatFailureCategory(t: TFunction, category: string): string {
	return t(`pages.integrations.executions.failureCategories.${category}`, category);
}
