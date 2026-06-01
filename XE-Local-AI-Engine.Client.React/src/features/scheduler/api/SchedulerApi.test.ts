import { describe, expect, it } from "vitest";

import {
	type ScheduledJobDto,
	type ScheduledJobRunDto,
	type ScheduledJobTemplateDto,
	toSaveScheduledJobRequest,
	toScheduledJob,
	toScheduledJobRun,
	toScheduledJobTemplate,
} from "@/features/scheduler/api/SchedulerApi";
import type { ScheduledJobFormValues } from "@/features/scheduler/models/SchedulerModels";

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

describe("toScheduledJobTemplate", () => {
	it("normalizes nullable fields and defaults the schedule-kind list", () => {
		const dto: ScheduledJobTemplateDto = {
			templateId: "cleanup",
			displayName: "Cleanup",
			description: "Removes old rows",
			supportedScheduleKinds: ["Cron", "SimpleInterval"],
			defaultScheduleKind: "Cron",
			defaultMisfirePolicy: "Smart",
			allowManualTrigger: true,
			allowAgentCreation: false,
			historyDetailLevel: "Summary",
		};

		const template = toScheduledJobTemplate(dto);

		expect(template.parameterSchema).toBeNull();
		expect(template.defaultParameters).toBeNull();
		expect(template.defaultMaxRuntimeSeconds).toBeNull();
		expect(template.supportedScheduleKinds).toEqual(["Cron", "SimpleInterval"]);
	});
});

describe("toScheduledJob", () => {
	it("maps a job, coalescing optional nullable fields and surfacing only hasParameters", () => {
		const dto: ScheduledJobDto = {
			id: "job-1",
			templateId: "cleanup",
			displayName: "Nightly cleanup",
			enabled: true,
			scheduleKind: "Cron",
			cronExpression: "0 0 3 * * ?",
			timeZoneId: "UTC",
			misfirePolicy: "Smart",
			preventOverlap: true,
			hasParameters: true,
			createdBy: "User",
			createdAtUtc: 1000,
			updatedAtUtc: 2000,
		};

		const job = toScheduledJob(dto);

		expect(job.description).toBe("");
		expect(job.intervalSeconds).toBeNull();
		expect(job.startAtUtc).toBeNull();
		expect(job.deletedAtUtc).toBeNull();
		expect(job.hasParameters).toBe(true);
		// The wire never carries the raw parameter JSON, so the domain model has no field to leak it.
		expect(job).not.toHaveProperty("parameters");
	});
});

describe("toScheduledJobRun", () => {
	it("maps a run, exposing only redacted summary + errorMessage", () => {
		const dto: ScheduledJobRunDto = {
			id: "run-1",
			scheduledJobId: "job-1",
			templateId: "cleanup",
			triggeredBy: "Schedule",
			status: "Failed",
			summary: "Removed 12 rows",
			errorMessage: "Disk full",
			createdAtUtc: 5000,
		};

		const run = toScheduledJobRun(dto);

		expect(run.summary).toBe("Removed 12 rows");
		expect(run.errorMessage).toBe("Disk full");
		expect(run.durationMs).toBeNull();
		expect(run.cancellationRequestedAtUtc).toBeNull();
		expect(run).not.toHaveProperty("detailsJson");
		expect(run).not.toHaveProperty("errorDetails");
	});
});

describe("toSaveScheduledJobRequest", () => {
	it("sends only cron fields for a cron schedule", () => {
		const request = toSaveScheduledJobRequest(baseForm());

		expect(request.cronExpression).toBe("0 0 3 * * ?");
		expect(request.intervalSeconds).toBeNull();
		expect(request.repeatCount).toBeNull();
	});

	it("sends only interval fields for an interval schedule", () => {
		const request = toSaveScheduledJobRequest(
			baseForm({ scheduleKind: "SimpleInterval", cronExpression: "0 0 3 * * ?", intervalSeconds: "300", repeatCount: "5" }),
		);

		expect(request.cronExpression).toBeNull();
		expect(request.intervalSeconds).toBe(300);
		expect(request.repeatCount).toBe(5);
	});

	it("parses the one-shot start time to epoch millis", () => {
		const request = toSaveScheduledJobRequest(
			baseForm({ scheduleKind: "OneShot", cronExpression: "", startAtUtc: "2026-01-01T03:00:00Z" }),
		);

		expect(request.startAtUtc).toBe(Date.parse("2026-01-01T03:00:00Z"));
		expect(request.cronExpression).toBeNull();
		expect(request.intervalSeconds).toBeNull();
	});

	it("nulls blank optional fields and defaults the time zone", () => {
		const request = toSaveScheduledJobRequest(baseForm({ timeZoneId: "  ", description: "  ", parameters: "  " }));

		expect(request.timeZoneId).toBe("UTC");
		expect(request.description).toBeNull();
		expect(request.parameters).toBeNull();
		expect(request.maxRuntimeSeconds).toBeNull();
	});

	it("forwards typed parameters verbatim", () => {
		const request = toSaveScheduledJobRequest(baseForm({ parameters: '{"key":"value"}' }));

		expect(request.parameters).toBe('{"key":"value"}');
	});
});
