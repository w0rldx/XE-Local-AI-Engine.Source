import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { MS_PER_DAY, startOfUtcDay, type UsageDateRange } from "@/core/usage/UsageDateRange";

import type {
	XeLocalAiEngineClientEndpointsAgentsV1AgentUsageProviderTotalsResponse as ProviderTotalsDto,
	XeLocalAiEngineClientEndpointsAgentsV1AgentUsageSummaryBucketResponse as UsageBucketDto,
	XeLocalAiEngineClientEndpointsAgentsV1AgentUsageSummaryResponse as UsageSummaryDto,
	XeLocalAiEngineClientEndpointsAgentsV1AgentUsageSummaryTotalsResponse as UsageTotalsDto,
} from "@/core/api/generated";

export type { ProviderTotalsDto, UsageBucketDto, UsageSummaryDto, UsageTotalsDto };

// One point on the daily time-series chart: the UTC-day bucket start (unix-ms, from the wire) and the total tokens
// summed across every (model, provider) bucket that fell on that day.
export interface UsageDailyPoint {
	readonly dayStartUtcMs: number;
	readonly totalTokens: number;
	readonly promptTokens: number;
	readonly completionTokens: number;
	// Null while no run in the group reported reasoning tokens: many local providers never report a count.
	readonly reasoningTokens: number | null;
	readonly runCount: number;
}

// One row of the per-model table: usage summed across every provider/day bucket for a single model, plus the
// distinct providers that model ran under (so the table can show "local, codex" when a model spans providers).
export interface UsageModelRow {
	readonly modelName: string;
	readonly providers: readonly string[];
	readonly runCount: number;
	readonly promptTokens: number;
	readonly completionTokens: number;
	// Null while no run in the group reported reasoning tokens: many local providers never report a count.
	readonly reasoningTokens: number | null;
	readonly totalTokens: number;
	// Runs that reported token usage. A run stopped before the model reported usage has none, so the sums above cover
	// only these runs: 0 means the row's tokens are not reported at all, fewer than runCount that some runs are missing.
	readonly tokensReported: number;
	// Estimated cost in USD, summed from the per-bucket server-computed values (never recomputed client-side). 0 for a
	// model that only ran on free/local/unpriced providers.
	readonly estimatedCostUsd: number;
}

// The canonical provider dimension emitted by the backend. `unknown` is the catch-all the backend assigns when a
// run predates the provider dimension or its provider could not be resolved.
const KNOWN_PROVIDERS = ["local", "ollama", "codex", "azure", "unknown"] as const;
export type KnownProvider = (typeof KNOWN_PROVIDERS)[number];

// True when the summary carries no recorded usage — drives the empty-state guidance card. Checks the grand total
// run count (the authoritative "nothing happened" signal) rather than the items array, so a range with buckets but
// zero runs (not expected from the backend, but defensive) still reads as empty.
export function isUsageEmpty(summary: UsageSummaryDto | undefined): boolean {
	return !summary || summary.totals.runCount === 0;
}

// Sums reported counts; stays null only while nothing in the group has been reported.
function addReported(sum: number | null, value: number | null | undefined): number | null {
	return sum === null ? (value ?? null) : sum + (value ?? 0);
}

// Aggregates the flat (model, provider, day) buckets into one point per UTC day, summing every token dimension and
// run count. Returned ascending by day so the line chart reads left→right in time order.
export function aggregateByDay(items: readonly UsageBucketDto[]): UsageDailyPoint[] {
	const byDay = new Map<number, UsageDailyPoint>();

	for (const bucket of items) {
		const existing = byDay.get(bucket.dayStartUtcMs);
		if (existing) {
			byDay.set(bucket.dayStartUtcMs, {
				dayStartUtcMs: bucket.dayStartUtcMs,
				totalTokens: existing.totalTokens + bucket.totalTokens,
				promptTokens: existing.promptTokens + bucket.promptTokens,
				completionTokens: existing.completionTokens + bucket.completionTokens,
				reasoningTokens: addReported(existing.reasoningTokens, bucket.reasoningTokens),
				runCount: existing.runCount + bucket.runCount,
			});
		} else {
			byDay.set(bucket.dayStartUtcMs, {
				dayStartUtcMs: bucket.dayStartUtcMs,
				totalTokens: bucket.totalTokens,
				promptTokens: bucket.promptTokens,
				completionTokens: bucket.completionTokens,
				reasoningTokens: bucket.reasoningTokens ?? null,
				runCount: bucket.runCount,
			});
		}
	}

	return [...byDay.values()].sort((a, b) => a.dayStartUtcMs - b.dayStartUtcMs);
}

// Aggregates the flat buckets into one row per model, summing token dimensions and collecting the distinct provider
// set. Returned descending by total tokens so the heaviest models surface first.
export function aggregateByModel(items: readonly UsageBucketDto[]): UsageModelRow[] {
	const byModel = new Map<string, UsageModelRow & { readonly providerSet: Set<string> }>();

	for (const bucket of items) {
		const existing = byModel.get(bucket.modelName);
		if (existing) {
			existing.providerSet.add(bucket.provider);
			byModel.set(bucket.modelName, {
				modelName: bucket.modelName,
				providerSet: existing.providerSet,
				providers: [...existing.providerSet].sort((a, b) => a.localeCompare(b)),
				runCount: existing.runCount + bucket.runCount,
				promptTokens: existing.promptTokens + bucket.promptTokens,
				completionTokens: existing.completionTokens + bucket.completionTokens,
				reasoningTokens: addReported(existing.reasoningTokens, bucket.reasoningTokens),
				totalTokens: existing.totalTokens + bucket.totalTokens,
				tokensReported: existing.tokensReported + bucket.tokensReported,
				estimatedCostUsd: existing.estimatedCostUsd + bucket.estimatedCostUsd,
			});
		} else {
			const providerSet = new Set<string>([bucket.provider]);
			byModel.set(bucket.modelName, {
				modelName: bucket.modelName,
				providerSet,
				providers: [...providerSet],
				runCount: bucket.runCount,
				promptTokens: bucket.promptTokens,
				completionTokens: bucket.completionTokens,
				reasoningTokens: bucket.reasoningTokens ?? null,
				totalTokens: bucket.totalTokens,
				tokensReported: bucket.tokensReported,
				estimatedCostUsd: bucket.estimatedCostUsd,
			});
		}
	}

	return [...byModel.values()]
		.map(({ providerSet: _providerSet, ...row }) => row)
		.sort((a, b) => b.totalTokens - a.totalTokens || a.modelName.localeCompare(b.modelName));
}

// Formats a UTC-day-start unix-ms as a short day label (e.g. "5/25") in the active UI language, for the x-axis / table.
export function formatDayLabel(dayStartUtcMs: number): string {
	return formatTimestamp(dayStartUtcMs, { month: "short", day: "numeric", timeZone: "UTC" });
}

// Date controls use whole UTC days and the backend's half-open [from, to) range; the default window and the query
// translation live in core/usage/UsageDateRange.ts, shared with the Home usage card.

// Converts a YYYY-MM-DD value (from a native date input, interpreted as a UTC calendar day) to its start-of-day
// unix-ms. Returns null for an empty/invalid value.
export function isoDateToUtcMs(value: string): number | null {
	if (!value) {
		return null;
	}
	const ms = Date.parse(`${value}T00:00:00.000Z`);
	return Number.isNaN(ms) ? null : ms;
}

// Formats a start-of-UTC-day unix-ms as the YYYY-MM-DD value a native date input expects.
export function utcMsToIsoDate(epochMs: number): string {
	return new Date(startOfUtcDay(epochMs)).toISOString().slice(0, 10);
}

// The earliest selectable UTC day given the retention window (start-of-day unix-ms), used as the date input `min`.
export function retentionFloorMs(nowMs: number, retentionDays: number): number {
	const days = Math.max(1, retentionDays);
	return startOfUtcDay(nowMs) - (days - 1) * MS_PER_DAY;
}

// Clamps a candidate range into [retentionFloor, today] and keeps from <= to. Used whenever the user edits either
// endpoint so an out-of-retention or inverted range can never reach the query.
export function clampDateRange(range: UsageDateRange, nowMs: number, retentionDays: number): UsageDateRange {
	const todayStart = startOfUtcDay(nowMs);
	const floor = retentionFloorMs(nowMs, retentionDays);
	const fromMs = Math.min(Math.max(range.fromMs, floor), todayStart);
	const toMs = Math.min(Math.max(range.toMs, fromMs), todayStart);
	return { fromMs, toMs };
}
