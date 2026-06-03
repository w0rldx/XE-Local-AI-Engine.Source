import { z } from "zod";

// Mirrors the backend scheduler enums. The wire contract serializes each enum as its STRING name (camelCase
// is used elsewhere, but enum members ride as their PascalCase names). Schedule kinds: Cron drives a cron
// expression; OneShot fires once at startAtUtc; SimpleInterval repeats every intervalSeconds; Manual is a
// durable on-demand job with no trigger (fired only via "Refresh now"/trigger), so it has no schedule fields.
export type ScheduleKind = "Cron" | "OneShot" | "SimpleInterval" | "Manual";

export const scheduleKinds: readonly ScheduleKind[] = ["Cron", "OneShot", "SimpleInterval", "Manual"];

// Misfire recovery policy applied by Quartz when a trigger is missed (node asleep / overloaded). Smart lets
// Quartz pick per trigger type; SkipMissed drops the missed fire; FireOnceNow fires a single catch-up run.
export type SchedulerMisfirePolicy = "Smart" | "SkipMissed" | "FireOnceNow";

export const schedulerMisfirePolicies: readonly SchedulerMisfirePolicy[] = ["Smart", "SkipMissed", "FireOnceNow"];

// Lifecycle status of one run. Active = Queued | Running (cancellable); the rest are terminal.
export type ScheduledRunStatus = "Queued" | "Running" | "Succeeded" | "Failed" | "Cancelled" | "TimedOut" | "Skipped";

export const scheduledRunStatuses: readonly ScheduledRunStatus[] = [
	"Queued",
	"Running",
	"Succeeded",
	"Failed",
	"Cancelled",
	"TimedOut",
	"Skipped",
];

// What initiated a run: the schedule itself, a manual trigger, an agent, or the system.
export type ScheduledRunTrigger = "Schedule" | "Manual" | "Agent" | "System";

// Who created the job definition.
export type ScheduledJobCreator = "User" | "Agent" | "System";

// True while a run can still be cancelled (it has not reached a terminal status).
export function isActiveRunStatus(status: ScheduledRunStatus): boolean {
	return status === "Queued" || status === "Running";
}

// Domain view-model for a scheduled job definition. Parameters are redacted on the wire (the response carries
// only hasParameters, never the raw JSON), so the model never holds a parameter value — the editor writes
// parameters but never reads them back. Timestamps are epoch milliseconds (long on the wire).
export interface ScheduledJob {
	readonly id: string;
	readonly templateId: string;
	readonly displayName: string;
	readonly description: string;
	readonly enabled: boolean;
	readonly scheduleKind: ScheduleKind;
	readonly cronExpression: string | null;
	readonly intervalSeconds: number | null;
	readonly repeatCount: number | null;
	readonly startAtUtc: number | null;
	readonly endAtUtc: number | null;
	readonly timeZoneId: string;
	readonly misfirePolicy: SchedulerMisfirePolicy;
	readonly preventOverlap: boolean;
	readonly maxRuntimeSeconds: number | null;
	readonly hasParameters: boolean;
	readonly createdBy: ScheduledJobCreator;
	readonly createdAtUtc: number;
	readonly updatedAtUtc: number;
	readonly disabledAtUtc: number | null;
	readonly deletedAtUtc: number | null;
}

// Domain view-model for one execution of a scheduled job. Run details are redacted on the wire (no detailsJson
// / errorDetails) — only the summary and errorMessage surface here.
export interface ScheduledJobRun {
	readonly id: string;
	readonly scheduledJobId: string;
	readonly templateId: string;
	readonly triggeredBy: ScheduledRunTrigger;
	readonly status: ScheduledRunStatus;
	readonly scheduledFireTimeUtc: number | null;
	readonly actualFireTimeUtc: number | null;
	readonly completedAtUtc: number | null;
	readonly durationMs: number | null;
	readonly summary: string | null;
	readonly errorMessage: string | null;
	readonly cancellationRequestedAtUtc: number | null;
	readonly createdAtUtc: number;
}

// Domain view-model for a job template. The template constrains which schedule kinds a job may use and supplies
// the defaults the create form pre-fills. parameterSchema/defaultParameters are opaque JSON strings (shown to
// the operator as hints, written verbatim into the parameters textarea).
export interface ScheduledJobTemplate {
	readonly templateId: string;
	readonly displayName: string;
	readonly description: string;
	readonly parameterSchema: string | null;
	readonly defaultParameters: string | null;
	readonly supportedScheduleKinds: readonly ScheduleKind[];
	readonly defaultScheduleKind: ScheduleKind;
	readonly defaultMisfirePolicy: SchedulerMisfirePolicy;
	readonly defaultMaxRuntimeSeconds: number | null;
	readonly allowManualTrigger: boolean;
	readonly allowAgentCreation: boolean;
	readonly historyDetailLevel: string;
}

// Optional filters for the run-history query. Empty filters list every run; each present filter narrows the
// server query (status, time window, single job). Times are epoch milliseconds.
export interface ScheduledJobRunFilters {
	readonly status?: ScheduledRunStatus;
	readonly fromUtc?: number;
	readonly toUtc?: number;
	readonly scheduledJobId?: string;
}

// Form values are narrower than the persisted entity: identity/timestamps/creator are backend-managed and the
// enabled flag is toggled by a separate row action (not edited here). Times are edited as plain strings (the
// datetime-local input value) and projected to epoch-millis on submit; numeric fields are edited as strings so
// a cleared input is "" rather than NaN. parameters is write-only plaintext JSON the operator types.
export interface ScheduledJobFormValues {
	templateId: string;
	displayName: string;
	description: string;
	scheduleKind: ScheduleKind;
	cronExpression: string;
	intervalSeconds: string;
	repeatCount: string;
	startAtUtc: string;
	endAtUtc: string;
	timeZoneId: string;
	misfirePolicy: SchedulerMisfirePolicy;
	preventOverlap: boolean;
	maxRuntimeSeconds: string;
	parameters: string;
}

const scheduleKindSchema = z.enum(["Cron", "OneShot", "SimpleInterval", "Manual"]);
const misfirePolicySchema = z.enum(["Smart", "SkipMissed", "FireOnceNow"]);

// Parses an optional positive integer from a form string. Returns undefined for a blank field, null for a value
// that is not a positive integer (so the schema can flag it). Kept separate so the API mapper can reuse it.
export function parsePositiveInteger(value: string): number | null | undefined {
	const trimmed = value.trim();
	if (trimmed.length === 0) {
		return undefined;
	}
	const parsed = Number(trimmed);
	if (!Number.isInteger(parsed) || parsed <= 0) {
		return null;
	}
	return parsed;
}

// Parses a datetime-local string ("YYYY-MM-DDTHH:mm") to epoch milliseconds. Returns undefined for a blank
// field, null for an unparseable value (so the schema can flag a malformed required start time).
export function parseDateTimeLocal(value: string): number | null | undefined {
	const trimmed = value.trim();
	if (trimmed.length === 0) {
		return undefined;
	}
	const parsed = Date.parse(trimmed);
	if (Number.isNaN(parsed)) {
		return null;
	}
	return parsed;
}

// Zod schema validating the form before submit. The form is a convenience — the backend re-validates and is
// authoritative — so this guards only the obvious shape errors per schedule kind: Cron needs a cron expression;
// SimpleInterval needs intervalSeconds > 0; OneShot needs a valid startAtUtc; Manual needs no schedule fields.
// maxRuntimeSeconds (when present) and repeatCount (when present) must be positive integers.
export const scheduledJobFormSchema = z
	.object({
		templateId: z.string().trim().min(1),
		displayName: z.string().trim().min(1).max(200),
		description: z.string().max(2000),
		scheduleKind: scheduleKindSchema,
		cronExpression: z.string(),
		intervalSeconds: z.string(),
		repeatCount: z.string(),
		startAtUtc: z.string(),
		endAtUtc: z.string(),
		timeZoneId: z.string().trim().min(1),
		misfirePolicy: misfirePolicySchema,
		preventOverlap: z.boolean(),
		maxRuntimeSeconds: z.string(),
		parameters: z.string(),
	})
	.superRefine((value, ctx) => {
		if (value.scheduleKind === "Cron") {
			if (value.cronExpression.trim().length === 0) {
				ctx.addIssue({ code: "custom", message: "A cron expression is required", path: ["cronExpression"] });
			}
		} else if (value.scheduleKind === "SimpleInterval") {
			const interval = parsePositiveInteger(value.intervalSeconds);
			if (interval === undefined) {
				ctx.addIssue({ code: "custom", message: "An interval is required", path: ["intervalSeconds"] });
			} else if (interval === null) {
				ctx.addIssue({
					code: "custom",
					message: "Interval must be a positive whole number of seconds",
					path: ["intervalSeconds"],
				});
			}
		} else if (value.scheduleKind === "OneShot") {
			const start = parseDateTimeLocal(value.startAtUtc);
			if (start === undefined) {
				ctx.addIssue({ code: "custom", message: "A start time is required", path: ["startAtUtc"] });
			} else if (start === null) {
				ctx.addIssue({ code: "custom", message: "Start time is invalid", path: ["startAtUtc"] });
			}
		}
		// Manual: a durable on-demand job has no cron/interval/start-at fields, so there is nothing to validate.

		if (parsePositiveInteger(value.repeatCount) === null) {
			ctx.addIssue({
				code: "custom",
				message: "Repeat count must be a positive whole number",
				path: ["repeatCount"],
			});
		}

		if (parsePositiveInteger(value.maxRuntimeSeconds) === null) {
			ctx.addIssue({
				code: "custom",
				message: "Max runtime must be a positive whole number of seconds",
				path: ["maxRuntimeSeconds"],
			});
		}

		if (value.endAtUtc.trim().length > 0 && parseDateTimeLocal(value.endAtUtc) === null) {
			ctx.addIssue({ code: "custom", message: "End time is invalid", path: ["endAtUtc"] });
		}
	});

export type ScheduledJobFormSchema = z.infer<typeof scheduledJobFormSchema>;
