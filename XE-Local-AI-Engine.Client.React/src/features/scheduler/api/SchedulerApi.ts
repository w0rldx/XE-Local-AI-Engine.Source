import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import {
	parseDateTimeLocal,
	parsePositiveInteger,
	type ScheduledJob,
	type ScheduledJobCreator,
	type ScheduledJobFormValues,
	type ScheduledJobRun,
	type ScheduledJobRunFilters,
	type ScheduledJobTemplate,
	type ScheduledRunStatus,
	type ScheduledRunTrigger,
	type ScheduleKind,
	type SchedulerMisfirePolicy,
} from "@/features/scheduler/models/SchedulerModels";

// Wire DTOs (camelCase, matching the other Local API surfaces). Kept as a thin contract layer so the page works
// against the documented Marker 3 endpoints; if the backend casing/route base differs, only this file changes.
// Enums ride as their string names. Sensitive fields are redacted on the wire: the job response carries only
// hasParameters (never the raw parameter JSON) and the run response carries only summary + errorMessage.
export interface ScheduledJobTemplateDto {
	templateId: string;
	displayName: string;
	description: string;
	parameterSchema?: string | null;
	defaultParameters?: string | null;
	supportedScheduleKinds: ScheduleKind[];
	defaultScheduleKind: ScheduleKind;
	defaultMisfirePolicy: SchedulerMisfirePolicy;
	defaultMaxRuntimeSeconds?: number | null;
	allowManualTrigger: boolean;
	allowAgentCreation: boolean;
	historyDetailLevel: string;
}

export interface ListScheduledJobTemplatesResponseDto {
	items: ScheduledJobTemplateDto[];
}

export interface ScheduledJobDto {
	id: string;
	templateId: string;
	displayName: string;
	description?: string | null;
	enabled: boolean;
	scheduleKind: ScheduleKind;
	cronExpression?: string | null;
	intervalSeconds?: number | null;
	repeatCount?: number | null;
	startAtUtc?: number | null;
	endAtUtc?: number | null;
	timeZoneId: string;
	misfirePolicy: SchedulerMisfirePolicy;
	preventOverlap: boolean;
	maxRuntimeSeconds?: number | null;
	hasParameters: boolean;
	createdBy: ScheduledJobCreator;
	createdAtUtc: number;
	updatedAtUtc: number;
	disabledAtUtc?: number | null;
	deletedAtUtc?: number | null;
}

export interface ListScheduledJobsResponseDto {
	items: ScheduledJobDto[];
}

export interface ScheduledJobRunDto {
	id: string;
	scheduledJobId: string;
	templateId: string;
	triggeredBy: ScheduledRunTrigger;
	status: ScheduledRunStatus;
	scheduledFireTimeUtc?: number | null;
	actualFireTimeUtc?: number | null;
	completedAtUtc?: number | null;
	durationMs?: number | null;
	summary?: string | null;
	errorMessage?: string | null;
	cancellationRequestedAtUtc?: number | null;
	createdAtUtc: number;
}

export interface ListScheduledJobRunsResponseDto {
	items: ScheduledJobRunDto[];
}

// Create and update share the same wire shape. parameters is write-only plaintext JSON; the response never
// echoes it back (only hasParameters). Times are epoch milliseconds. timeZoneId defaults to UTC and
// misfirePolicy to Smart on the backend when omitted; we always send the form's explicit values.
export interface SaveScheduledJobRequestDto {
	templateId: string;
	displayName: string;
	description: string | null;
	scheduleKind: ScheduleKind;
	cronExpression: string | null;
	intervalSeconds: number | null;
	repeatCount: number | null;
	startAtUtc: number | null;
	endAtUtc: number | null;
	timeZoneId: string;
	misfirePolicy: SchedulerMisfirePolicy;
	preventOverlap: boolean;
	maxRuntimeSeconds: number | null;
	parameters: string | null;
}

// 202 body when a cancel is accepted, or 409 body when the run is already terminal.
export interface ScheduledJobRunCancelResponseDto {
	outcome: string;
	cancellationRequestedAtUtc?: number | null;
}

// Scheduler route base. Single source so a route mismatch from Marker 3 is a one-line change.
const SCHEDULER_ROUTE = "scheduler";

export function toScheduledJobTemplate(dto: ScheduledJobTemplateDto): ScheduledJobTemplate {
	return {
		templateId: dto.templateId,
		displayName: dto.displayName,
		description: dto.description,
		parameterSchema: dto.parameterSchema ?? null,
		defaultParameters: dto.defaultParameters ?? null,
		supportedScheduleKinds: dto.supportedScheduleKinds ?? [],
		defaultScheduleKind: dto.defaultScheduleKind,
		defaultMisfirePolicy: dto.defaultMisfirePolicy,
		defaultMaxRuntimeSeconds: dto.defaultMaxRuntimeSeconds ?? null,
		allowManualTrigger: dto.allowManualTrigger,
		allowAgentCreation: dto.allowAgentCreation,
		historyDetailLevel: dto.historyDetailLevel,
	};
}

export function toScheduledJob(dto: ScheduledJobDto): ScheduledJob {
	return {
		id: dto.id,
		templateId: dto.templateId,
		displayName: dto.displayName,
		description: dto.description ?? "",
		enabled: dto.enabled,
		scheduleKind: dto.scheduleKind,
		cronExpression: dto.cronExpression ?? null,
		intervalSeconds: dto.intervalSeconds ?? null,
		repeatCount: dto.repeatCount ?? null,
		startAtUtc: dto.startAtUtc ?? null,
		endAtUtc: dto.endAtUtc ?? null,
		timeZoneId: dto.timeZoneId,
		misfirePolicy: dto.misfirePolicy,
		preventOverlap: dto.preventOverlap,
		maxRuntimeSeconds: dto.maxRuntimeSeconds ?? null,
		hasParameters: dto.hasParameters,
		createdBy: dto.createdBy,
		createdAtUtc: dto.createdAtUtc,
		updatedAtUtc: dto.updatedAtUtc,
		disabledAtUtc: dto.disabledAtUtc ?? null,
		deletedAtUtc: dto.deletedAtUtc ?? null,
	};
}

export function toScheduledJobRun(dto: ScheduledJobRunDto): ScheduledJobRun {
	return {
		id: dto.id,
		scheduledJobId: dto.scheduledJobId,
		templateId: dto.templateId,
		triggeredBy: dto.triggeredBy,
		status: dto.status,
		scheduledFireTimeUtc: dto.scheduledFireTimeUtc ?? null,
		actualFireTimeUtc: dto.actualFireTimeUtc ?? null,
		completedAtUtc: dto.completedAtUtc ?? null,
		durationMs: dto.durationMs ?? null,
		summary: dto.summary ?? null,
		errorMessage: dto.errorMessage ?? null,
		cancellationRequestedAtUtc: dto.cancellationRequestedAtUtc ?? null,
		createdAtUtc: dto.createdAtUtc,
	};
}

// Projects form values to the wire request. Only the fields relevant to the chosen schedule kind are sent so a
// stored job never carries cross-kind leftovers (e.g. a cron string on an interval job). Numeric/time fields
// are parsed from their string form; a blank optional field becomes null. parameters is sent only when the
// operator typed something.
export function toSaveScheduledJobRequest(form: ScheduledJobFormValues): SaveScheduledJobRequestDto {
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

export async function listScheduledJobTemplates(config?: AxiosRequestConfig): Promise<ScheduledJobTemplate[]> {
	const { data } = await axiosInstance.get<ListScheduledJobTemplatesResponseDto>(
		buildLocalApiUrl(`${SCHEDULER_ROUTE}/templates`),
		config,
	);
	return (data.items ?? []).map(toScheduledJobTemplate);
}

export async function listScheduledJobs(
	includeDeleted = false,
	config?: AxiosRequestConfig,
): Promise<ScheduledJob[]> {
	const { data } = await axiosInstance.get<ListScheduledJobsResponseDto>(buildLocalApiUrl(`${SCHEDULER_ROUTE}/jobs`), {
		...config,
		params: { ...config?.params, includeDeleted },
	});
	return (data.items ?? []).map(toScheduledJob);
}

export async function getScheduledJob(id: string, config?: AxiosRequestConfig): Promise<ScheduledJob> {
	const { data } = await axiosInstance.get<ScheduledJobDto>(
		buildLocalApiUrl(`${SCHEDULER_ROUTE}/jobs/${encodeURIComponent(id)}`),
		config,
	);
	return toScheduledJob(data);
}

export async function createScheduledJob(
	request: SaveScheduledJobRequestDto,
	config?: AxiosRequestConfig,
): Promise<ScheduledJob> {
	const { data } = await axiosInstance.post<ScheduledJobDto>(
		buildLocalApiUrl(`${SCHEDULER_ROUTE}/jobs`),
		request,
		config,
	);
	return toScheduledJob(data);
}

export async function updateScheduledJob(
	id: string,
	request: SaveScheduledJobRequestDto,
	config?: AxiosRequestConfig,
): Promise<ScheduledJob> {
	const { data } = await axiosInstance.put<ScheduledJobDto>(
		buildLocalApiUrl(`${SCHEDULER_ROUTE}/jobs/${encodeURIComponent(id)}`),
		request,
		config,
	);
	return toScheduledJob(data);
}

export async function enableScheduledJob(id: string, config?: AxiosRequestConfig): Promise<void> {
	await axiosInstance.post(buildLocalApiUrl(`${SCHEDULER_ROUTE}/jobs/${encodeURIComponent(id)}/enable`), null, config);
}

export async function disableScheduledJob(id: string, config?: AxiosRequestConfig): Promise<void> {
	await axiosInstance.post(buildLocalApiUrl(`${SCHEDULER_ROUTE}/jobs/${encodeURIComponent(id)}/disable`), null, config);
}

export async function deleteScheduledJob(id: string, config?: AxiosRequestConfig): Promise<void> {
	await axiosInstance.delete(buildLocalApiUrl(`${SCHEDULER_ROUTE}/jobs/${encodeURIComponent(id)}`), config);
}

export async function triggerScheduledJob(id: string, config?: AxiosRequestConfig): Promise<void> {
	await axiosInstance.post(buildLocalApiUrl(`${SCHEDULER_ROUTE}/jobs/${encodeURIComponent(id)}/trigger`), null, config);
}

export async function listScheduledJobRuns(
	filters: ScheduledJobRunFilters = {},
	config?: AxiosRequestConfig,
): Promise<ScheduledJobRun[]> {
	const { data } = await axiosInstance.get<ListScheduledJobRunsResponseDto>(buildLocalApiUrl(`${SCHEDULER_ROUTE}/runs`), {
		...config,
		// Drop undefined filters so the query string only carries the active ones.
		params: {
			...config?.params,
			...(filters.status !== undefined ? { status: filters.status } : {}),
			...(filters.fromUtc !== undefined ? { fromUtc: filters.fromUtc } : {}),
			...(filters.toUtc !== undefined ? { toUtc: filters.toUtc } : {}),
			...(filters.scheduledJobId !== undefined ? { scheduledJobId: filters.scheduledJobId } : {}),
		},
	});
	return (data.items ?? []).map(toScheduledJobRun);
}

export async function getScheduledJobRun(runId: string, config?: AxiosRequestConfig): Promise<ScheduledJobRun> {
	const { data } = await axiosInstance.get<ScheduledJobRunDto>(
		buildLocalApiUrl(`${SCHEDULER_ROUTE}/runs/${encodeURIComponent(runId)}`),
		config,
	);
	return toScheduledJobRun(data);
}

export async function cancelScheduledJobRun(
	runId: string,
	config?: AxiosRequestConfig,
): Promise<ScheduledJobRunCancelResponseDto> {
	const { data } = await axiosInstance.post<ScheduledJobRunCancelResponseDto>(
		buildLocalApiUrl(`${SCHEDULER_ROUTE}/runs/${encodeURIComponent(runId)}/cancel`),
		null,
		config,
	);
	return data;
}
