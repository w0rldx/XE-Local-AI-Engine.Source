import { z } from "zod";

// Domain models for the External Integrations admin surface. The wire enums ride as their PascalCase member names
// (like the scheduler's), with ONE exception: acceptedInputKinds crosses as a string[] of LOWERCASE member names
// ("text", "json") — IntegrationMapper.TextInputKind/JsonInputKind on the backend. Timestamps are epoch
// milliseconds (long on the wire).

/** How a trigger resolves the conversation an invocation runs in. */
export type IntegrationSessionPolicy = "PerInvocation" | "CallerManaged";

export const integrationSessionPolicies: readonly IntegrationSessionPolicy[] = ["PerInvocation", "CallerManaged"];

/** The input payload kinds a trigger accepts. Never empty — the backend validator rejects an empty array. */
export type IntegrationInputKind = "text" | "json";

/**
 * External-facing trigger slug. It appears verbatim in the integrator's invoke URL, so a typo here is a 404 the
 * caller sees — which is why the editor validates this one field on change rather than on submit.
 */
export const integrationTriggerNamePattern = /^[a-z0-9][a-z0-9-]{1,63}$/;

/** Domain view-model for one trigger. */
export interface IntegrationTrigger {
	readonly id: string;
	readonly name: string;
	readonly displayName: string;
	readonly description: string;
	readonly enabled: boolean;
	readonly targetAgentDefinitionId: string;
	readonly sessionPolicy: IntegrationSessionPolicy;
	readonly acceptedInputKinds: readonly IntegrationInputKind[];
	readonly createdAtUtc: number;
	readonly updatedAtUtc: number;
	/** Optimistic-concurrency token echoed back as expectedVersion on update. */
	readonly version: number;
}

/**
 * Domain view-model for one API key. `principalId` is the stable integrator identity (sessions, executions,
 * rate-limit partition and 404 masking all key on it); `keyPrefix` is audit detail for one credential, so two keys
 * sharing a principal are one integrator with two credentials. `allowedTriggerIds === null` is the "all triggers"
 * wildcard — an EMPTY array is not the same thing and never means "all".
 */
export interface IntegrationApiKey {
	readonly id: string;
	readonly principalId: string;
	readonly keyPrefix: string;
	readonly label: string;
	readonly allowedTriggerIds: readonly string[] | null;
	readonly createdAtUtc: number;
	readonly lastUsedAtUtc: number | null;
	readonly revokedAtUtc: number | null;
}

/** Form state for the trigger editor. All fields are strings/booleans so the inputs stay controlled. */
export interface IntegrationTriggerFormValues {
	readonly name: string;
	readonly displayName: string;
	readonly description: string;
	readonly enabled: boolean;
	readonly targetAgentDefinitionId: string;
	readonly sessionPolicy: IntegrationSessionPolicy;
	readonly acceptsText: boolean;
	readonly acceptsJson: boolean;
}

export const emptyIntegrationTriggerFormValues: IntegrationTriggerFormValues = {
	name: "",
	displayName: "",
	description: "",
	enabled: true,
	targetAgentDefinitionId: "",
	sessionPolicy: "PerInvocation",
	acceptsText: true,
	acceptsJson: false,
};

const sessionPolicySchema = z.enum(["PerInvocation", "CallerManaged"]);

export const integrationTriggerFormSchema = z
	.object({
		name: z.string().regex(integrationTriggerNamePattern, "nameFormat"),
		// The two length caps restate IntegrationTriggerValidationRules.MaxDisplayNameLength/MaxDescriptionLength: a
		// client maximum above the server's turns a fixable form error into a late 400.
		displayName: z.string().trim().min(1, "displayNameRequired").max(128, "displayNameTooLong"),
		description: z.string().max(1024, "descriptionTooLong"),
		enabled: z.boolean(),
		targetAgentDefinitionId: z.string().trim().min(1, "targetRequired"),
		sessionPolicy: sessionPolicySchema,
		acceptsText: z.boolean(),
		acceptsJson: z.boolean(),
	})
	.superRefine((value, ctx) => {
		if (!(value.acceptsText || value.acceptsJson)) {
			ctx.addIssue({ code: "custom", message: "inputKindRequired", path: ["acceptedInputKinds"] });
		}
	});

/** Form state for the generate-key dialog. `principalId` is empty for the "New identity" default. */
export interface IntegrationKeyFormValues {
	readonly label: string;
	readonly principalId: string;
	readonly allowAllTriggers: boolean;
	readonly allowedTriggerIds: readonly string[];
}

export const emptyIntegrationKeyFormValues: IntegrationKeyFormValues = {
	label: "",
	principalId: "",
	allowAllTriggers: false,
	allowedTriggerIds: [],
};

// The allowlist rule is written the safe way round: only the explicit switch produces the "all triggers" wildcard,
// and an untouched multiselect is a validation error rather than a silent grant of every trigger on the node.
export const integrationKeyFormSchema = z
	.object({
		// 128 is the backend's MaxDisplayNameLength, which GenerateIntegrationApiKeyRequestValidator applies to the label.
		label: z.string().trim().min(1, "labelRequired").max(128, "labelTooLong"),
		principalId: z.string(),
		allowAllTriggers: z.boolean(),
		allowedTriggerIds: z.array(z.string()),
	})
	.superRefine((value, ctx) => {
		if (!value.allowAllTriggers && value.allowedTriggerIds.length === 0) {
			ctx.addIssue({ code: "custom", message: "triggersRequired", path: ["allowedTriggerIds"] });
		}
	});

/** One selectable agent in the trigger editor's target picker. */
export interface IntegrationAgentOption {
	readonly id: string;
	readonly name: string;
	readonly description: string;
	readonly allowedToolNames: readonly string[];
	readonly toolApprovals: Readonly<Record<string, boolean>>;
}

/** The catalog facts the approval banner and the CallerManaged preflight both read, keyed by tool name. */
export interface IntegrationToolFacts {
	readonly effectiveRequiresApproval: boolean;
	readonly category: string;
}

/** Lifecycle state of one execution. `Queued` appears only when the run actually waited for the node's lease. */
export type IntegrationExecutionStatus = "Accepted" | "Queued" | "Running" | "Completed" | "Failed" | "Cancelled";

export const integrationExecutionStatuses: readonly IntegrationExecutionStatus[] = [
	"Accepted",
	"Queued",
	"Running",
	"Completed",
	"Failed",
	"Cancelled",
];

/**
 * True while a run can still change state, which is the ONLY thing this predicate decides: whether a row offers the
 * cancel action. It deliberately does not gate the page's polling — polling is unconditional, because this predicate
 * reads the very list a poll would have to fetch, so an empty or all-terminal window would switch the refresh off and
 * a run started elsewhere would never appear.
 */
export function isActiveExecutionStatus(status: IntegrationExecutionStatus): boolean {
	return status === "Accepted" || status === "Queued" || status === "Running";
}

/** Lifecycle state of one caller-managed session. */
export type IntegrationSessionStatus = "Active" | "Closed";

export const integrationSessionStatuses: readonly IntegrationSessionStatus[] = ["Active", "Closed"];

/**
 * The maximum both list validators accept (ListIntegrationExecutionsRequestValidator.MaxLimit and
 * ListIntegrationSessionsRequestValidator.MaxLimit). Neither list response carries a total count, so the pages ask
 * for one bounded window at this size and say so, rather than drawing a page navigator over a count they would have
 * to invent.
 */
export const integrationListLimit = 200;

/**
 * The maximum ListIntegrationExecutionEventsRequestValidator accepts. The timeline re-reads the whole event list on
 * every tick (`sinceSeq: 0`), so it asks for the largest page the endpoint will serve rather than the default 200 —
 * events ascend by sequence, so a truncated read would drop the terminal event, which is the one row that says how
 * the run ended.
 */
export const integrationEventLimit = 500;

/** Domain view-model for one execution row (the list's summary projection). */
export interface IntegrationExecution {
	readonly id: string;
	readonly triggerId: string;
	readonly sessionId: string;
	readonly status: IntegrationExecutionStatus;
	readonly receivedAtUtc: number;
	readonly startedAtUtc: number | null;
	readonly endedAtUtc: number | null;
	/**
	 * One of the backend's closed set of ten categories, rendered VERBATIM. No locale map and no icon per value: a
	 * category the client does not recognise must still reach the operator rather than blanking the cell.
	 */
	readonly failureCategory: string | null;
	readonly failureSummary: string | null;
	readonly outputCount: number;
}

/** The extra audit fields only the per-execution read carries; `principalId` names the integrator that invoked it. */
export interface IntegrationExecutionDetail {
	readonly execution: IntegrationExecution;
	readonly principalId: string;
	readonly keyPrefix: string;
	readonly requestId: string;
	readonly invocationId: string;
	readonly outputBytes: number;
	readonly stopRequestedAtUtc: number | null;
}

/**
 * One persisted timeline event. `sequence` values ascend but may SKIP — a failed durable write leaves a permanent
 * hole — so the timeline renders what it receives and never treats a gap as a missing row.
 */
export interface IntegrationExecutionEvent {
	readonly sequence: number;
	readonly eventType: string;
	readonly detailJson: string | null;
	readonly occurredAtUtc: number;
}

/** Domain view-model for one session row. `triggerName` is empty only when the trigger has since been deleted. */
export interface IntegrationSession {
	readonly id: string;
	readonly triggerId: string;
	readonly triggerName: string;
	/** The integrator that owns the session. Stable across a key rotation, so it is the identity, not the credential. */
	readonly principalId: string;
	readonly agentDefinitionId: string;
	readonly status: IntegrationSessionStatus;
	readonly createdAtUtc: number;
	readonly lastActivityUtc: number;
	readonly executionCount: number;
}

// Both filter shapes carry EXACTLY the query parameters their endpoint accepts. An absent field is left undefined and
// never sent; nothing is filtered or re-sorted in the browser, because both lists are a server-bounded window and a
// client-side filter over one hides rows that match but fall outside it.

export interface IntegrationExecutionFilters {
	readonly triggerId?: string;
	readonly sessionId?: string;
	readonly status?: IntegrationExecutionStatus;
}

export interface IntegrationSessionFilters {
	readonly triggerId?: string;
	readonly status?: IntegrationSessionStatus;
}
