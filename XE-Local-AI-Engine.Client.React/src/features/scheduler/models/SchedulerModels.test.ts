import { describe, expect, it } from "vitest";

import {
	isActiveRunStatus,
	parseDateTimeLocal,
	parsePositiveInteger,
	type ScheduledJobFormValues,
	scheduledJobFormSchema,
} from "@/features/scheduler/models/SchedulerModels";

function baseForm(overrides: Partial<ScheduledJobFormValues> = {}): ScheduledJobFormValues {
	return {
		templateId: "cleanup",
		displayName: "Nightly cleanup",
		description: "",
		scheduleKind: "Cron",
		cronExpression: "0 0 3 * * ?",
		intervalSeconds: "",
		repeatCount: "",
		startAtUtc: "",
		endAtUtc: "",
		timeZoneId: "UTC",
		misfirePolicy: "Smart",
		preventOverlap: true,
		maxRuntimeSeconds: "",
		parameters: "",
		...overrides,
	};
}

function issuePaths(form: ScheduledJobFormValues): string[] {
	const result = scheduledJobFormSchema.safeParse(form);
	if (result.success) {
		return [];
	}
	return result.error.issues.map((issue) => issue.path.join("."));
}

describe("parsePositiveInteger", () => {
	it.each([
		["", undefined],
		["  ", undefined],
		["10", 10],
		["0", null],
		["-3", null],
		["1.5", null],
		["abc", null],
	])("parses %s", (value, expected) => {
		expect(parsePositiveInteger(value)).toBe(expected);
	});
});

describe("parseDateTimeLocal", () => {
	it("returns undefined for a blank value", () => {
		expect(parseDateTimeLocal("")).toBeUndefined();
	});

	it("returns null for an unparseable value", () => {
		expect(parseDateTimeLocal("not a date")).toBeNull();
	});

	it("parses an ISO datetime-local value to epoch millis", () => {
		expect(parseDateTimeLocal("2026-01-01T00:00:00Z")).toBe(Date.parse("2026-01-01T00:00:00Z"));
	});
});

describe("isActiveRunStatus", () => {
	it.each([
		["Queued", true],
		["Running", true],
		["Succeeded", false],
		["Failed", false],
		["Cancelled", false],
		["TimedOut", false],
		["Skipped", false],
	] as const)("classifies %s as active=%s", (status, expected) => {
		expect(isActiveRunStatus(status)).toBe(expected);
	});
});

describe("scheduledJobFormSchema", () => {
	it("accepts a valid cron job", () => {
		expect(issuePaths(baseForm())).toEqual([]);
	});

	it("accepts a valid interval job", () => {
		const form = baseForm({ scheduleKind: "SimpleInterval", cronExpression: "", intervalSeconds: "300" });
		expect(issuePaths(form)).toEqual([]);
	});

	it("accepts a valid one-shot job", () => {
		const form = baseForm({ scheduleKind: "OneShot", cronExpression: "", startAtUtc: "2026-01-01T03:00" });
		expect(issuePaths(form)).toEqual([]);
	});

	it("accepts a manual job with no schedule fields", () => {
		const form = baseForm({ scheduleKind: "Manual", cronExpression: "", intervalSeconds: "", startAtUtc: "" });
		expect(issuePaths(form)).toEqual([]);
	});

	it("rejects an empty display name", () => {
		expect(issuePaths(baseForm({ displayName: "  " }))).toContain("displayName");
	});

	it("rejects an empty template", () => {
		expect(issuePaths(baseForm({ templateId: "  " }))).toContain("templateId");
	});

	it("requires a cron expression for cron schedules", () => {
		expect(issuePaths(baseForm({ cronExpression: "  " }))).toContain("cronExpression");
	});

	it("requires an interval for interval schedules", () => {
		const form = baseForm({ scheduleKind: "SimpleInterval", cronExpression: "", intervalSeconds: "" });
		expect(issuePaths(form)).toContain("intervalSeconds");
	});

	it("rejects a non-positive interval", () => {
		const form = baseForm({ scheduleKind: "SimpleInterval", cronExpression: "", intervalSeconds: "0" });
		expect(issuePaths(form)).toContain("intervalSeconds");
	});

	it("requires a start time for one-shot schedules", () => {
		const form = baseForm({ scheduleKind: "OneShot", cronExpression: "", startAtUtc: "" });
		expect(issuePaths(form)).toContain("startAtUtc");
	});

	it("rejects an invalid one-shot start time", () => {
		const form = baseForm({ scheduleKind: "OneShot", cronExpression: "", startAtUtc: "not a date" });
		expect(issuePaths(form)).toContain("startAtUtc");
	});

	it("rejects a non-positive repeat count", () => {
		const form = baseForm({ scheduleKind: "SimpleInterval", cronExpression: "", intervalSeconds: "60", repeatCount: "0" });
		expect(issuePaths(form)).toContain("repeatCount");
	});

	it("rejects a non-positive max runtime", () => {
		expect(issuePaths(baseForm({ maxRuntimeSeconds: "-1" }))).toContain("maxRuntimeSeconds");
	});

	it("rejects an invalid end time when present", () => {
		expect(issuePaths(baseForm({ endAtUtc: "not a date" }))).toContain("endAtUtc");
	});
});
