import { describe, expect, it } from "vitest";

import { defaultDateRange, MS_PER_DAY, startOfUtcDay, toQueryRange } from "@/core/usage/UsageDateRange";

const DAY_1 = Date.UTC(2026, 4, 24); // 2026-05-24T00:00Z
const DAY_2 = Date.UTC(2026, 4, 25);

describe("UsageDateRange", () => {
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

	it("toQueryRange makes the upper bound half-open (end day + 1)", () => {
		expect(toQueryRange({ fromMs: DAY_1, toMs: DAY_2 })).toEqual({ fromEpochMs: DAY_1, toEpochMs: DAY_2 + MS_PER_DAY });
	});

	it("startOfUtcDay floors an instant to its UTC day", () => {
		expect(startOfUtcDay(now)).toBe(DAY_2);
	});
});
