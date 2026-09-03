import { describe, expect, it } from "vitest";

import type { UsageBucketDto, UsageSummaryDto } from "@/features/usage-dashboard/models/UsageDashboardModel";
import {
	aggregateByDay,
	aggregateByModel,
	clampDateRange,
	defaultDateRange,
	formatCostUsd,
	formatCount,
	formatTokensCompact,
	isUsageEmpty,
	isoDateToUtcMs,
	retentionFloorMs,
	startOfUtcDay,
	toQueryRange,
	utcMsToIsoDate,
} from "@/features/usage-dashboard/models/UsageDashboardModel";

const MS_PER_DAY = 86_400_000;
const DAY_1 = Date.UTC(2026, 4, 24); // 2026-05-24T00:00Z
const DAY_2 = Date.UTC(2026, 4, 25);

function bucket(overrides: Partial<UsageBucketDto>): UsageBucketDto {
	return {
		modelName: "qwen3:8b",
		provider: "local",
		dayStartUtcMs: DAY_1,
		runCount: 1,
		promptTokens: 10,
		completionTokens: 20,
		reasoningTokens: 5,
		totalTokens: 35,
		estimatedCostUsd: 0,
		currency: "USD",
		...overrides,
	};
}

describe("UsageDashboardModel aggregation", () => {
	it("aggregateByDay sums every dimension per day and returns ascending by day", () => {
		const daily = aggregateByDay([
			bucket({ dayStartUtcMs: DAY_2, totalTokens: 100, promptTokens: 40, completionTokens: 50, reasoningTokens: 10, runCount: 2 }),
			bucket({ dayStartUtcMs: DAY_1, provider: "codex", totalTokens: 35 }),
			bucket({ dayStartUtcMs: DAY_1, provider: "local", totalTokens: 35 }),
		]);

		expect(daily.map((d) => d.dayStartUtcMs)).toEqual([DAY_1, DAY_2]);
		expect(daily[0]?.totalTokens).toBe(70);
		expect(daily[0]?.runCount).toBe(2);
		expect(daily[1]?.totalTokens).toBe(100);
	});

	it("aggregateByModel sums per model, collects distinct providers, and sorts by total tokens desc", () => {
		const rows = aggregateByModel([
			bucket({ modelName: "qwen3:8b", provider: "local", totalTokens: 35 }),
			bucket({ modelName: "qwen3:8b", provider: "codex", totalTokens: 15 }),
			bucket({ modelName: "gpt-5", provider: "codex", totalTokens: 200 }),
		]);

		expect(rows.map((r) => r.modelName)).toEqual(["gpt-5", "qwen3:8b"]);
		const qwen = rows.find((r) => r.modelName === "qwen3:8b");
		expect(qwen?.totalTokens).toBe(50);
		expect(qwen?.providers).toEqual(["codex", "local"]);
	});

	it("aggregateByModel sums the server-computed per-bucket cost per model", () => {
		const rows = aggregateByModel([
			bucket({ modelName: "gpt-5", provider: "codex", estimatedCostUsd: 1.25 }),
			bucket({ modelName: "gpt-5", provider: "codex", estimatedCostUsd: 0.75 }),
			bucket({ modelName: "qwen3:8b", provider: "local", estimatedCostUsd: 0 }),
		]);

		expect(rows.find((r) => r.modelName === "gpt-5")?.estimatedCostUsd).toBeCloseTo(2.0, 10);
		// A local-only model stays at zero cost (free/unpriced).
		expect(rows.find((r) => r.modelName === "qwen3:8b")?.estimatedCostUsd).toBe(0);
	});

	it("isUsageEmpty is true for undefined and for zero-run totals", () => {
		expect(isUsageEmpty(undefined)).toBe(true);
		const summary: UsageSummaryDto = {
			items: [],
			totals: { runCount: 0, promptTokens: 0, completionTokens: 0, reasoningTokens: 0, totalTokens: 0, estimatedCostUsd: 0, currency: "USD" },
			byProvider: [],
			retentionDays: 30,
		};
		expect(isUsageEmpty(summary)).toBe(true);
		expect(isUsageEmpty({ ...summary, totals: { ...summary.totals, runCount: 3 } })).toBe(false);
	});
});

describe("UsageDashboardModel formatting", () => {
	it("formatTokensCompact abbreviates large counts", () => {
		expect(formatTokensCompact(999)).toBe("999");
		expect(formatTokensCompact(1_500_000)).toMatch(/1\.5M/);
	});

	it("formatCount groups thousands", () => {
		// Grouping separator is locale-dependent; assert the digits survive and a separator is present.
		expect(formatCount(1234567)).toMatch(/1\D?234\D?567/);
	});

	it("formatCostUsd renders an em-dash for free/unpriced and a currency value otherwise", () => {
		// Zero / non-positive = free/local/unpriced -> em-dash, not "$0.00".
		expect(formatCostUsd(0)).toBe("—");
		expect(formatCostUsd(-1)).toBe("—");
		// A positive cost renders as a USD currency value (symbol + the digits, locale-formatted).
		expect(formatCostUsd(12.5)).toMatch(/\$?12[.,]50/);
		// A priced-but-tiny value still shows a currency value (distinct from the free em-dash).
		expect(formatCostUsd(0.004)).toMatch(/0[.,]00/);
	});
});

describe("UsageDashboardModel date range", () => {
	const now = DAY_2 + 12 * 3_600_000;

	it("defaultDateRange spans 30 days by default, ending today", () => {
		const range = defaultDateRange(now);
		expect(range.toMs).toBe(DAY_2);
		expect(range.fromMs).toBe(DAY_2 - 29 * MS_PER_DAY);
	});

	it("defaultDateRange narrows to a shorter retention window", () => {
		const range = defaultDateRange(now, 7);
		expect(range.fromMs).toBe(DAY_2 - 6 * MS_PER_DAY);
	});

	it("clampDateRange keeps the range within retention and ordered", () => {
		const floor = retentionFloorMs(now, 7);
		const clamped = clampDateRange({ fromMs: floor - 10 * MS_PER_DAY, toMs: now + MS_PER_DAY }, now, 7);
		expect(clamped.fromMs).toBe(floor);
		expect(clamped.toMs).toBe(DAY_2);
		// inverted input (from after to) collapses to from <= to
		const inverted = clampDateRange({ fromMs: DAY_2, toMs: DAY_1 }, now, 30);
		expect(inverted.toMs).toBeGreaterThanOrEqual(inverted.fromMs);
	});

	it("toQueryRange makes the upper bound half-open (end day + 1)", () => {
		expect(toQueryRange({ fromMs: DAY_1, toMs: DAY_2 })).toEqual({ fromEpochMs: DAY_1, toEpochMs: DAY_2 + MS_PER_DAY });
	});

	it("iso <-> utc-ms round trip on day boundaries", () => {
		expect(isoDateToUtcMs("2026-05-25")).toBe(DAY_2);
		expect(utcMsToIsoDate(DAY_2)).toBe("2026-05-25");
		expect(startOfUtcDay(now)).toBe(DAY_2);
		expect(isoDateToUtcMs("")).toBeNull();
	});
});
