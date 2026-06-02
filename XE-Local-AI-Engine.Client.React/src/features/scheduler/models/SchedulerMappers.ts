import type {
	XeLocalAiEngineClientEndpointsSchedulerV1CreateScheduledJobRequest,
	XeLocalAiEngineClientEndpointsSchedulerV1ScheduledJobResponse,
	XeLocalAiEngineClientEndpointsSchedulerV1ScheduledJobRunResponse,
	XeLocalAiEngineClientEndpointsSchedulerV1ScheduledJobTemplateResponse,
} from "@/core/api/generated";
import {
	parseDateTimeLocal,
	parsePositiveInteger,
	type ScheduledJob,
	type ScheduledJobCreator,
	type ScheduledJobFormValues,
	type ScheduledJobRun,
	type ScheduledJobTemplate,
	type ScheduledRunStatus,
	type ScheduledRunTrigger,
	type ScheduleKind,
	type SchedulerMisfirePolicy,
} from "@/features/scheduler/models/SchedulerModels";

// Maps the generated (OpenAPI) scheduler response types to the stricter domain view-models the components depend
// on. The generated types are the single source of truth for the wire shape; their fields are all optional
// (`x?: T`), so each mapper coalesces every field to a required value with a sensible default. The generated
// enums are string unions with the SAME values as the domain unions, so an enum maps through unchanged when
// present and falls back to a safe default when omitted. Redaction is unchanged — the backend already drops the
// sensitive fields, so only hasParameters / summary / errorMessage ever surface here.

// Defaults mirror the backend's omit-on-default behavior (timeZoneId → UTC, misfirePolicy → Smart) and keep the
// domain types total even when the server omits an optional field.
const DEFAULT_SCHEDULE_KIND: ScheduleKind = "Cron";
const DEFAULT_MISFIRE_POLICY: SchedulerMisfirePolicy = "Smart";
const DEFAULT_RUN_STATUS: ScheduledRunStatus = "Queued";
const DEFAULT_RUN_TRIGGER: ScheduledRunTrigger = "Schedule";
const DEFAULT_JOB_CREATOR: ScheduledJobCreator = "User";
const DEFAULT_TIME_ZONE = "UTC";

export function toScheduledJobTemplate(
	dto: XeLocalAiEngineClientEndpointsSchedulerV1ScheduledJobTemplateResponse,
): ScheduledJobTemplate {
	return {
		templateId: dto.templateId ?? "",
		displayName: dto.displayName ?? "",
		description: dto.description ?? "",
		parameterSchema: dto.parameterSchema ?? null,
		defaultParameters: dto.defaultParameters ?? null,
		supportedScheduleKinds: dto.supportedScheduleKinds ?? [],
		defaultScheduleKind: dto.defaultScheduleKind ?? DEFAULT_SCHEDULE_KIND,
		defaultMisfirePolicy: dto.defaultMisfirePolicy ?? DEFAULT_MISFIRE_POLICY,
		defaultMaxRuntimeSeconds: dto.defaultMaxRuntimeSeconds ?? null,
		allowManualTrigger: dto.allowManualTrigger ?? false,
		allowAgentCreation: dto.allowAgentCreation ?? false,
		historyDetailLevel: dto.historyDetailLevel ?? "",
	};
}

export function toScheduledJob(dto: XeLocalAiEngineClientEndpointsSchedulerV1ScheduledJobResponse): ScheduledJob {
	return {
		id: dto.id ?? "",
		templateId: dto.templateId ?? "",
		displayName: dto.displayName ?? "",
		description: dto.description ?? "",
		enabled: dto.enabled ?? false,
		scheduleKind: dto.scheduleKind ?? DEFAULT_SCHEDULE_KIND,
		cronExpression: dto.cronExpression ?? null,
		intervalSeconds: dto.intervalSeconds ?? null,
		repeatCount: dto.repeatCount ?? null,
		startAtUtc: dto.startAtUtc ?? null,
		endAtUtc: dto.endAtUtc ?? null,
		timeZoneId: dto.timeZoneId ?? DEFAULT_TIME_ZONE,
		misfirePolicy: dto.misfirePolicy ?? DEFAULT_MISFIRE_POLICY,
		preventOverlap: dto.preventOverlap ?? false,
		maxRuntimeSeconds: dto.maxRuntimeSeconds ?? null,
		hasParameters: dto.hasParameters ?? false,
		createdBy: dto.createdBy ?? DEFAULT_JOB_CREATOR,
		createdAtUtc: dto.createdAtUtc ?? 0,
		updatedAtUtc: dto.updatedAtUtc ?? 0,
		disabledAtUtc: dto.disabledAtUtc ?? null,
		deletedAtUtc: dto.deletedAtUtc ?? null,
	};
}

export function toScheduledJobRun(
	dto: XeLocalAiEngineClientEndpointsSchedulerV1ScheduledJobRunResponse,
): ScheduledJobRun {
	return {
		id: dto.id ?? "",
		scheduledJobId: dto.scheduledJobId ?? "",
		templateId: dto.templateId ?? "",
		triggeredBy: dto.triggeredBy ?? DEFAULT_RUN_TRIGGER,
		status: dto.status ?? DEFAULT_RUN_STATUS,
		scheduledFireTimeUtc: dto.scheduledFireTimeUtc ?? null,
		actualFireTimeUtc: dto.actualFireTimeUtc ?? null,
		completedAtUtc: dto.completedAtUtc ?? null,
		durationMs: dto.durationMs ?? null,
		summary: dto.summary ?? null,
		errorMessage: dto.errorMessage ?? null,
		cancellationRequestedAtUtc: dto.cancellationRequestedAtUtc ?? null,
		createdAtUtc: dto.createdAtUtc ?? 0,
	};
}

// Projects form values to the generated create/update request body. Create and update share the same wire shape
// (verified against the generated CreateScheduledJobRequest / UpdateScheduledJobRequest — structurally identical),
// so one mapper serves both. Only the fields relevant to the chosen schedule kind are sent so a stored job never
// carries cross-kind leftovers (e.g. a cron string on an interval job). Numeric/time fields are parsed from their
// string form; a blank optional field becomes null. parameters is sent only when the operator typed something.
export function toSaveScheduledJobRequest(
	form: ScheduledJobFormValues,
): XeLocalAiEngineClientEndpointsSchedulerV1CreateScheduledJobRequest {
	const trimmedDescription = form.description.trim();
	const trimmedParameters = form.parameters.trim();
	const isCron = form.scheduleKind === "Cron";
	const isInterval = form.scheduleKind === "SimpleInterval";

	return {
		templateId: form.templateId.trim(),
		displayName: form.displayName.trim(),
		description: trimmedDescription.length > 0 ? trimmedDescription : null,
		scheduleKind: form.scheduleKind,
		cronExpression: isCron && form.cronExpression.trim().length > 0 ? form.cronExpression.trim() : null,
		intervalSeconds: isInterval ? (parsePositiveInteger(form.intervalSeconds) ?? null) : null,
		repeatCount: isInterval ? (parsePositiveInteger(form.repeatCount) ?? null) : null,
		// startAtUtc is required for OneShot but optional for cron/interval (a "not before" anchor), so it is
		// always sent when the operator provided a value rather than being gated on the schedule kind.
		startAtUtc: parseDateTimeLocal(form.startAtUtc) ?? null,
		endAtUtc: parseDateTimeLocal(form.endAtUtc) ?? null,
		timeZoneId: form.timeZoneId.trim().length > 0 ? form.timeZoneId.trim() : "UTC",
		misfirePolicy: form.misfirePolicy,
		preventOverlap: form.preventOverlap,
		maxRuntimeSeconds: parsePositiveInteger(form.maxRuntimeSeconds) ?? null,
		parameters: trimmedParameters.length > 0 ? trimmedParameters : null,
	};
}
