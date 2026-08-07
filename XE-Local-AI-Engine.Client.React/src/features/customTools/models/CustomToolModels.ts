import { z } from "zod";

// Domain view-models + form schema for the node-authored custom-tool library. Custom tools run commands, call
// networks, and launch programs on the host, so this feature is deliberately blunt about the danger and gates Save
// behind an explicit acknowledgement. The backend (CustomToolService.ValidateAsync) stays authoritative; the checks
// here mirror it so an obviously-invalid definition fails the same way client- and server-side.

export type CustomToolKind = "HttpFetch" | "Command";
export type CustomToolMode = "Fixed" | "Parameterized";
export type CustomToolParameterType = "string" | "number" | "integer" | "boolean";

// The reserved MAF/OpenAI tool-name prefix every custom tool carries (mirrors CustomToolValidation.ToolNamePrefix).
// The operator authors only the slug; the backend prepends this if absent and the model sees the full `custom__{slug}`.
export const CUSTOM_TOOL_NAME_PREFIX = "custom__";

// Slug rule mirrors the backend regex `^custom__[a-z0-9](?:[a-z0-9_]{0,48}[a-z0-9])?$` minus the prefix: lowercase,
// starts and ends alphanumeric, underscores allowed inside, 1–50 chars.
export const CUSTOM_TOOL_SLUG_PATTERN = /^[a-z0-9](?:[a-z0-9_]{0,48}[a-z0-9])?$/;
// Parameter names are `[A-Za-z_][A-Za-z0-9_]*` identifiers (CustomToolService.IdentifierRegex).
export const CUSTOM_TOOL_PARAM_NAME_PATTERN = /^[A-Za-z_][A-Za-z0-9_]*$/;

export const CUSTOM_TOOL_DESCRIPTION_MAX = 1024;
export const CUSTOM_TOOL_PARAM_NAME_MAX = 64;
// HostProcessExecutor.MaxTimeoutSeconds; 0 means "use the executor default".
export const CUSTOM_TOOL_TIMEOUT_MAX = 300;

export const CUSTOM_TOOL_PARAMETER_TYPES: readonly CustomToolParameterType[] = ["string", "number", "integer", "boolean"];
export const CUSTOM_TOOL_HTTP_METHODS = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"] as const;

// Sentinel a masked secret round-trips as. A read never returns a stored secret value; the API replaces every secret
// header/env value with this. On edit the form keeps it untouched, and the backend resolves it back to the stored
// value — only a value the operator actually changes is persisted as a new secret.
export const CUSTOM_TOOL_SECRET_SENTINEL = "__secret_set__";

export interface CustomToolParameter {
	readonly name: string;
	readonly type: CustomToolParameterType;
	readonly description: string;
	readonly required: boolean;
}

export interface CustomToolHeader {
	readonly name: string;
	readonly value: string;
	readonly isSecret: boolean;
}

export interface CustomToolEnvVar {
	readonly name: string;
	readonly value: string;
	readonly isSecret: boolean;
}

export interface CustomToolHttpDefinition {
	readonly method: string;
	readonly urlTemplate: string;
	readonly headers: readonly CustomToolHeader[];
	readonly bodyTemplate: string;
	readonly allowedHosts: readonly string[];
}

export interface CustomToolCommandDefinition {
	readonly executable: string;
	readonly argsTemplate: readonly string[];
	readonly workingDirectory: string;
	readonly timeoutSeconds: number;
	readonly env: readonly CustomToolEnvVar[];
}

// Full record as shown in the list/editor. Secret header/env values arrive masked as CUSTOM_TOOL_SECRET_SENTINEL.
export interface CustomToolView {
	readonly id: string;
	readonly name: string;
	readonly description: string;
	readonly kind: CustomToolKind;
	readonly mode: CustomToolMode;
	readonly enabled: boolean;
	readonly acknowledged: boolean;
	readonly version: number;
	readonly createdAtUtc: number;
	readonly updatedAtUtc: number;
	readonly parameters: readonly CustomToolParameter[];
	readonly http: CustomToolHttpDefinition | null;
	readonly command: CustomToolCommandDefinition | null;
}

// Form carries both editors regardless of the active kind so switching kind never loses the other's draft. Only the
// active kind's block is sent on submit. `name` here is the SLUG (no `custom__` prefix — that is fixed adornment).
export interface CustomToolFormValues {
	name: string;
	description: string;
	kind: CustomToolKind;
	mode: CustomToolMode;
	enabled: boolean;
	acknowledged: boolean;
	parameters: CustomToolParameter[];
	http: CustomToolHttpDefinition;
	command: CustomToolCommandDefinition;
}

const parameterSchema = z.object({
	name: z.string().trim().min(1).max(CUSTOM_TOOL_PARAM_NAME_MAX).regex(CUSTOM_TOOL_PARAM_NAME_PATTERN, { message: "paramNameInvalid" }),
	type: z.enum(CUSTOM_TOOL_PARAMETER_TYPES),
	description: z.string().trim(),
	required: z.boolean(),
});

const headerSchema = z.object({
	name: z.string().trim().min(1),
	value: z.string(),
	isSecret: z.boolean(),
});

const envSchema = z.object({
	name: z.string().trim().min(1).regex(CUSTOM_TOOL_PARAM_NAME_PATTERN, { message: "envNameInvalid" }),
	value: z.string(),
	isSecret: z.boolean(),
});

// Submit-time validation. Cross-field rules (Fixed forbids parameters; each kind requires its own block) are enforced
// with superRefine so the error attaches to a stable path the form can surface. The backend re-checks everything.
export const customToolFormSchema = z
	.object({
		name: z.string().trim().min(1).regex(CUSTOM_TOOL_SLUG_PATTERN, { message: "nameInvalid" }),
		description: z.string().trim().min(1).max(CUSTOM_TOOL_DESCRIPTION_MAX),
		kind: z.enum(["HttpFetch", "Command"]),
		mode: z.enum(["Fixed", "Parameterized"]),
		enabled: z.boolean(),
		// The danger acknowledgement is mandatory: the server enforces it on every create/update, so a false here can
		// never save. Gating Save on it in the UI just makes the requirement visible before the round-trip.
		acknowledged: z.literal(true),
		parameters: z.array(parameterSchema),
		http: z.object({
			method: z.string().trim().min(1),
			urlTemplate: z.string().trim().min(1),
			headers: z.array(headerSchema),
			bodyTemplate: z.string(),
			allowedHosts: z.array(z.string().trim().min(1)),
		}),
		command: z.object({
			executable: z.string().trim().min(1),
			argsTemplate: z.array(z.string()),
			workingDirectory: z.string(),
			timeoutSeconds: z.number().int().min(0).max(CUSTOM_TOOL_TIMEOUT_MAX),
			env: z.array(envSchema),
		}),
	})
	.superRefine((values, ctx) => {
		if (values.mode === "Fixed" && values.parameters.length > 0) {
			ctx.addIssue({ code: z.ZodIssueCode.custom, message: "fixedNoParameters", path: ["mode"] });
		}
		if (values.kind === "HttpFetch" && values.http.urlTemplate.trim().length === 0) {
			ctx.addIssue({ code: z.ZodIssueCode.custom, message: "urlRequired", path: ["http", "urlTemplate"] });
		}
		if (values.kind === "Command" && values.command.executable.trim().length === 0) {
			ctx.addIssue({ code: z.ZodIssueCode.custom, message: "executableRequired", path: ["command", "executable"] });
		}
	});

export type CustomToolFormSchema = z.infer<typeof customToolFormSchema>;

/** Strips the `custom__` prefix so the editor works with the bare slug. */
export function toSlug(name: string): string {
	return name.startsWith(CUSTOM_TOOL_NAME_PREFIX) ? name.slice(CUSTOM_TOOL_NAME_PREFIX.length) : name;
}
