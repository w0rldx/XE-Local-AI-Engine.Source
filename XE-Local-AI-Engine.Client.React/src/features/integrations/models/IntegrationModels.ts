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

/**
 * What reaching a tool does to a run with nobody behind it. Read verbatim off the catalog's closed value set
 * (`ToolUnattendedBehaviourValues`) and NOT derivable from the approval flag: `ask_user` is approval-gated too, but an
 * unattended run continues past it with the question unanswered. A value this client does not know stays a plain
 * string and is treated as failing, which is the safe direction.
 */
export const integrationToolContinuesUnanswered = "continuesUnanswered";

/** The catalog facts the unattended-approval warning reads, keyed by tool name. */
export interface IntegrationToolFacts {
	readonly effectiveRequiresApproval: boolean;
	readonly category: string;
	readonly unattendedBehaviour: string;
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
	return activeIntegrationExecutionStatuses.includes(status);
}

/**
 * The three states a run passes through before it terminalises. They travel together as ONE filter chip: the endpoint
 * takes a repeated `status` parameter, so "everything in flight" is a question the server can answer rather than a
 * union the browser would have to assemble out of a bounded window.
 */
export const activeIntegrationExecutionStatuses: readonly IntegrationExecutionStatus[] = [
	"Accepted",
	"Queued",
	"Running",
];

/** Lifecycle state of one caller-managed session. */
export type IntegrationSessionStatus = "Active" | "Closed";

export const integrationSessionStatuses: readonly IntegrationSessionStatus[] = ["Active", "Closed"];

/**
 * The maximum both list validators accept (ListIntegrationExecutionsRequestValidator.MaxLimit and
 * ListIntegrationSessionsRequestValidator.MaxLimit), and therefore the largest page the pager may ask for.
 */
export const integrationListLimit = 200;

/**
 * Rows per page on first render, and the reason the list is paged at all: both list responses now carry a
 * `totalCount`, so the tables ask for a page an operator can actually read and let the pager reach the rest. 200 rows
 * behind a page navigator would be a pager nobody uses.
 */
export const integrationPageSize = 50;

/** Sizes the pager offers, capped by what the list validators accept. */
export const integrationPageSizeOptions: readonly number[] = [25, 50, 100, integrationListLimit];

/**
 * The maximum ListIntegrationExecutionEventsRequestValidator accepts, and therefore the timeline's PAGE size: the
 * hook walks the watermark until a page comes back short, so this is how few round-trips a long log costs, not how
 * much of it is readable. Events ascend by sequence, so stopping at one page would drop the terminal event, which
 * is the one row that says how the run ended.
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
	/** A SET, sent as a repeated `status` parameter. One chip can therefore stand for the three active states. */
	readonly status?: readonly IntegrationExecutionStatus[];
}

export interface IntegrationSessionFilters {
	readonly triggerId?: string;
	readonly status?: IntegrationSessionStatus;
}
