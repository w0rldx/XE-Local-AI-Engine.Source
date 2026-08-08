import { describe, expect, it } from "vitest";

import { formatModelFitReleaseDate } from "@/features/model-fit/components/ModelFitFormatters";

describe("formatModelFitReleaseDate", () => {
	it("formats a date-only ISO string as a locale-aware, date-only value", () => {
		expect(formatModelFitReleaseDate("2025-03-12")).toBe("Mar 12, 2025");
	});

	it("keeps the calendar day stable regardless of time zone (no `new Date(\"YYYY-MM-DD\")` UTC-midnight shift)", () => {
		// Parsed from the year/month/day parts via the local Date constructor, so the day never rolls back a date.
		expect(formatModelFitReleaseDate("2025-01-01")).toBe("Jan 1, 2025");
	});

	it("takes the leading date from an ISO datetime string", () => {
		expect(formatModelFitReleaseDate("2024-11-05T23:30:00Z")).toBe("Nov 5, 2024");
	});

	it("returns the em-dash placeholder for a null value", () => {
		expect(formatModelFitReleaseDate(null)).toBe("—");
	});

	it("returns the em-dash placeholder (never 'Invalid Date') for an unparsable string", () => {
		expect(formatModelFitReleaseDate("not-a-date")).toBe("—");
	});

	it("returns the em-dash placeholder for out-of-range parts that would otherwise roll over into a wrong day", () => {
		expect(formatModelFitReleaseDate("2025-13-40")).toBe("—");
	});
});
