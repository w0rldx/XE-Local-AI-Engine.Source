import type { ScheduledJob, ScheduledJobFormValues } from "@/features/scheduler/models/SchedulerModels";

export const emptySchedulerFormValues: ScheduledJobFormValues = {
	templateId: "",
	displayName: "",
	description: "",
	scheduleKind: "Cron",
	cronExpression: "",
	intervalSeconds: "",
	repeatCount: "",
	startAtUtc: "",
	endAtUtc: "",
	timeZoneId: "UTC",
	misfirePolicy: "Smart",
	preventOverlap: true,
	maxRuntimeSeconds: "",
	parameters: "",
};

function toDateTimeLocal(value: number | null): string {
	if (value === null) {
		return "";
	}
	const date = new Date(value);
	if (Number.isNaN(date.getTime())) {
		return "";
	}
	const offset = date.getTimezoneOffset() * 60000;
	return new Date(date.getTime() - offset).toISOString().slice(0, 16);
}

export function toSchedulerFormValues(job: ScheduledJob): ScheduledJobFormValues {
	return {
		templateId: job.templateId,
		displayName: job.displayName,
		description: job.description,
		scheduleKind: job.scheduleKind,
		cronExpression: job.cronExpression ?? "",
		intervalSeconds: job.intervalSeconds !== null ? String(job.intervalSeconds) : "",
		repeatCount: job.repeatCount !== null ? String(job.repeatCount) : "",
		startAtUtc: toDateTimeLocal(job.startAtUtc),
		endAtUtc: toDateTimeLocal(job.endAtUtc),
		timeZoneId: job.timeZoneId,
		misfirePolicy: job.misfirePolicy,
		preventOverlap: job.preventOverlap,
		maxRuntimeSeconds: job.maxRuntimeSeconds !== null ? String(job.maxRuntimeSeconds) : "",
		parameters: "",
	};
}
