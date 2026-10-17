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
const CUSTOM_TOOL_SLUG_PATTERN = /^[a-z0-9](?:[a-z0-9_]{0,48}[a-z0-9])?$/;
// Parameter names are `[A-Za-z_][A-Za-z0-9_]*` identifiers (CustomToolService.IdentifierRegex).
const CUSTOM_TOOL_PARAM_NAME_PATTERN = /^[A-Za-z_][A-Za-z0-9_]*$/;

const CUSTOM_TOOL_DESCRIPTION_MAX = 1024;
const CUSTOM_TOOL_PARAM_NAME_MAX = 64;
// The node's customToolMaxTimeoutSeconds default, used until the node settings have loaded; 0 means "use the executor
// default". The node setting is the real ceiling (useCustomToolMaxTimeoutSeconds) and the backend re-checks it.
export const CUSTOM_TOOL_TIMEOUT_MAX_FALLBACK = 300;

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
	// Hosts inside the private LAN ranges this tool may reach (ADR 0021); each is also an allowed host.
	readonly privateHosts: readonly string[];
}

export interface CustomToolCommandDefinition {
	readonly executable: string;
	readonly argsTemplate: readonly string[];
	readonly workingDirectory: string;
	readonly timeoutSeconds: number;
	readonly env: readonly CustomToolEnvVar[];
	// Launch and do not wait (a GUI program); the backend requires timeoutSeconds 0 then.
	readonly detach: boolean;
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
	readonly allowUnattended: boolean;
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
	// A graph workflow may run this exact invocation with nobody present; the backend requires Fixed mode for it.
	allowUnattended: boolean;
	parameters: CustomToolParameter[];
	http: CustomToolHttpDefinition;
	command: CustomToolCommandDefinition;
}

const parameterSchema = z.object({
	name: z
		.string()
		.trim()
		.min(1)
		.max(CUSTOM_TOOL_PARAM_NAME_MAX)
		.regex(CUSTOM_TOOL_PARAM_NAME_PATTERN, { message: "paramNameInvalid" }),
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
// Built per ceiling because the command-timeout maximum is a node setting, not a constant.
export const buildCustomToolFormSchema = (maxTimeoutSeconds: number = CUSTOM_TOOL_TIMEOUT_MAX_FALLBACK) =>
	z
		.object({
			name: z.string().trim().min(1).regex(CUSTOM_TOOL_SLUG_PATTERN, { message: "nameInvalid" }),
			description: z.string().trim().min(1).max(CUSTOM_TOOL_DESCRIPTION_MAX),
			kind: z.enum(["HttpFetch", "Command"]),
			mode: z.enum(["Fixed", "Parameterized"]),
			enabled: z.boolean(),
			// The danger acknowledgement is mandatory: the server enforces it on every create/update, so a false here can
			// never save. Gating Save on it in the UI just makes the requirement visible before the round-trip.
			acknowledged: z.literal(true),
			allowUnattended: z.boolean(),
			parameters: z.array(parameterSchema),
			http: z.object({
				method: z.string().trim().min(1),
				// Required only for the ACTIVE kind — see superRefine. Only the active kind's block is submitted
				// (toDefinition) and only its editor is rendered, so requiring this unconditionally made the empty
				// create form unsavable with the error attached to an off-screen field.
				urlTemplate: z.string().trim(),
				headers: z.array(headerSchema),
				bodyTemplate: z.string(),
				allowedHosts: z.array(z.string().trim().min(1)),
				privateHosts: z.array(z.string().trim()),
			}),
			command: z.object({
				// Required only for the ACTIVE kind — see superRefine, and the urlTemplate note above.
				executable: z.string().trim(),
				argsTemplate: z.array(z.string()),
				workingDirectory: z.string(),
				timeoutSeconds: z.number().int().min(0).max(maxTimeoutSeconds),
				env: z.array(envSchema),
				detach: z.boolean(),
			}),
		})
		.superRefine((values, ctx) => {
			if (values.allowUnattended && values.mode !== "Fixed") {
				ctx.addIssue({ code: z.ZodIssueCode.custom, message: "unattendedNeedsFixed", path: ["allowUnattended"] });
			}
			if (values.kind === "HttpFetch") {
				values.http.privateHosts.forEach((host, index) => {
					const issue = privateHostIssue(host, values.http.allowedHosts);
					if (issue) {
						ctx.addIssue({ code: z.ZodIssueCode.custom, message: issue, path: ["http", "privateHosts", index] });
					}
				});
			}
			if (values.mode === "Fixed" && values.parameters.length > 0) {
				ctx.addIssue({ code: z.ZodIssueCode.custom, message: "fixedNoParameters", path: ["mode"] });
			}
			if (values.kind === "HttpFetch" && values.http.urlTemplate.trim().length === 0) {
				ctx.addIssue({ code: z.ZodIssueCode.custom, message: "urlRequired", path: ["http", "urlTemplate"] });
			}
			if (values.kind === "Command" && values.command.executable.trim().length === 0) {
				ctx.addIssue({ code: z.ZodIssueCode.custom, message: "executableRequired", path: ["command", "executable"] });
			}
			// Mirrors CustomToolService: a detached launch is not waited for, so it has no timeout.
			if (values.kind === "Command" && values.command.detach && values.command.timeoutSeconds !== 0) {
				ctx.addIssue({ code: z.ZodIssueCode.custom, message: "detachNeedsNoTimeout", path: ["command", "timeoutSeconds"] });
			}
		});

export type PrivateHostIssue = "privateHostInvalid" | "privateHostAlwaysDenied" | "privateHostNotAllowed";

const IPV4_PATTERN = /^(?:0|[1-9]\d{0,2})(?:\.(?:0|[1-9]\d{0,2})){3}$/;
const DNS_NAME_PATTERN = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$/;

type Ipv4Octets = readonly [number, number, number, number];

function ipv4Octets(host: string): Ipv4Octets | undefined {
	if (!IPV4_PATTERN.test(host)) {
		return undefined;
	}
	const [a = 0, b = 0, c = 0, d = 0] = host.split(".").map(Number);
	const octets: Ipv4Octets = [a, b, c, d];
	return octets.every((octet) => octet <= 255) ? octets : undefined;
}

// Mirrors CustomToolSsrfGuard.IsAlwaysDeniedAddress for IPv4: denied, and outside the LAN ranges a listed host may reach.
function isAlwaysDeniedIpv4([a, b, c]: Ipv4Octets): boolean {
	const lan = a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168) || (a === 100 && b >= 64 && b <= 127);
	if (lan) {
		return false;
	}
	return (
		a === 0 ||
		a === 127 ||
		(a === 169 && b === 254) ||
		(a === 192 && b === 0 && c === 0) ||
		(a === 198 && (b === 18 || b === 19)) ||
		a >= 224
	);
}

// The IPv6 side of the same rule: only unique-local (fc00::/7) is a LAN range; the deny-listed rest stays denied.
function isAlwaysDeniedIpv6(host: string): boolean {
	const mapped = /^::ffff:(.+)$/.exec(host);
	if (mapped) {
		const octets = ipv4Octets(mapped[1] ?? "");
		return octets ? isAlwaysDeniedIpv4(octets) : true;
	}
	// ::, ::1 and the IPv4-compatible ::/96 forms; link-local and site-local (fe80::/9..fec0::/10); multicast; NAT64.
	return host.startsWith("::") || /^(?:ff|fe[89a-f])/.test(host) || host.startsWith("64:ff9b::");
}

/**
 * Client mirror of the backend's private-host save rule: an exact host name or canonical IP literal (no scheme, port,
 * path, credentials or wildcard), never an always-denied address, and also present in allowedHosts. The backend
 * (CustomToolService) stays authoritative.
 */
export function privateHostIssue(entry: string, allowedHosts: readonly string[]): PrivateHostIssue | undefined {
	const host = entry.trim();
	const octets = ipv4Octets(host);
	let alwaysDenied = false;
	if (octets) {
		alwaysDenied = isAlwaysDeniedIpv4(octets);
	} else if (host.includes(":")) {
		let canonical: string | undefined;
		try {
			canonical = new URL(`http://[${host}]/`).hostname;
		} catch {
			canonical = undefined;
		}
		const isMappedV4 = /^::ffff:\d+\.\d+\.\d+\.\d+$/i.test(host);
		if (!isMappedV4 && canonical !== `[${host.toLowerCase()}]`) {
			return "privateHostInvalid";
		}
		alwaysDenied = isAlwaysDeniedIpv6(host.toLowerCase());
	} else if (!DNS_NAME_PATTERN.test(host) || /^[\d.]+$/.test(host) || /0x/i.test(host)) {
		return "privateHostInvalid";
	}
	if (alwaysDenied) {
		return "privateHostAlwaysDenied";
	}
	const listed = allowedHosts.some(
		(allowed) =>
			allowed
				.trim()
				.replace(/^\[|\]$/g, "")
				.toLowerCase() === host.toLowerCase(),
	);
	return listed ? undefined : "privateHostNotAllowed";
}

export type CustomToolFormSchema = z.infer<ReturnType<typeof buildCustomToolFormSchema>>;

/** Strips the `custom__` prefix so the editor works with the bare slug. */
export function toSlug(name: string): string {
	return name.startsWith(CUSTOM_TOOL_NAME_PREFIX) ? name.slice(CUSTOM_TOOL_NAME_PREFIX.length) : name;
}

/** One name/value/isSecret row in the HTTP-header and command-env editors. */
export interface SecretRow {
	readonly name: string;
	readonly value: string;
	readonly isSecret: boolean;
}

/**
 * The row names the node holds a stored secret for, read off the values it answered with (masked). Taken from the
 * form's INITIAL values, never its live ones: a cleared row carries "", exactly like a row that never had a value,
 * and the editors are unmounted and rebuilt whenever the tool kind is switched.
 */
export function storedSecretRowNames(rows: readonly SecretRow[]): readonly string[] {
	return rows.filter((row) => row.isSecret && row.value === CUSTOM_TOOL_SECRET_SENTINEL).map((row) => row.name);
}
