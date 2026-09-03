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
