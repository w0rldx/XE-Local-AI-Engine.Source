// The usage-summary query window in whole UTC days and the backend's half-open [from, to) range. Shared by the usage
// dashboard and the Home usage card so both ask for the same default window and their totals agree.

export const MS_PER_DAY = 86_400_000;

export const DEFAULT_RANGE_DAYS = 30;

export interface UsageDateRange {
	// Inclusive first UTC day (start-of-day unix-ms) — sent as fromEpochMs.
	readonly fromMs: number;
	// Inclusive last UTC day the user selected (start-of-day unix-ms). Sent as toEpochMs after adding one day so the
	// backend's half-open [from, to) range covers the whole selected end day.
	readonly toMs: number;
}

// Start-of-UTC-day (00:00:00.000Z) for the given instant.
export function startOfUtcDay(epochMs: number): number {
	return Math.floor(epochMs / MS_PER_DAY) * MS_PER_DAY;
}

// Default range: the last `min(DEFAULT_RANGE_DAYS, retentionDays)` whole UTC days ending today (inclusive). When
// retention is unknown (first render, before the summary loads) it falls back to DEFAULT_RANGE_DAYS. `nowMs` is a
// parameter so the pure function stays testable.
export function defaultDateRange(nowMs: number, retentionDays?: number): UsageDateRange {
	const spanDays = Math.max(1, Math.min(DEFAULT_RANGE_DAYS, retentionDays ?? DEFAULT_RANGE_DAYS));
	const todayStart = startOfUtcDay(nowMs);
	return {
		fromMs: todayStart - (spanDays - 1) * MS_PER_DAY,
		toMs: todayStart,
	};
}

// Translates the inclusive UTC-day range into the query params the backend expects: fromEpochMs is the from-day
// start; toEpochMs is the day AFTER the selected end day (half-open upper bound).
export function toQueryRange(range: UsageDateRange): { fromEpochMs: number; toEpochMs: number } {
	return { fromEpochMs: range.fromMs, toEpochMs: range.toMs + MS_PER_DAY };
}
