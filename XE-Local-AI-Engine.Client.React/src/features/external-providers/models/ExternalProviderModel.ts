import type { ReasoningEffort } from "@/core/models/ReasoningEffort";

// The declared trust flag on a connection. Mirrors the backend `ExternalProviderLocality` enum name, which is what
// the save request carries and the connection response echoes.
//
// It is OPERATOR-DECLARED, never inferred from the address: "Local" grants the connection full local-model parity
// (workspace/file tools, knowledge base, custom tools, run_python, dev mode), "Cloud" puts it under the existing
// cloud gating. That is why declaring Local for an address that is not on this machine or this network warrants the
// warning below rather than a silent downgrade — the node has no way to check the claim.
export type ExternalProviderLocality = "Local" | "Cloud";

const LOCALITIES: readonly ExternalProviderLocality[] = ["Local", "Cloud"];

function isLocality(value: string): value is ExternalProviderLocality {
	return (LOCALITIES as readonly string[]).includes(value);
}

// Narrows a stored/segmented-control string to a known locality. Anything unrecognized resolves to "Cloud" — the
// fail-closed direction, identical to the backend mapper's ParseLocality, so an unreadable value can never be
// presented as the more privileged of the two.
export function parseLocality(value: string | null | undefined): ExternalProviderLocality {
	return value !== null && value !== undefined && isLocality(value) ? value : "Cloud";
}

// One editable model row. Numeric fields are held as strings because they are text inputs: a blank string is
// "unspecified" (the backend then falls back to its own defaults), which a number cannot express.
export interface ExternalProviderModelDraft {
	wireId: string;
	displayName: string;
	contextLength: string;
	supportsTools: boolean;
	supportsVision: boolean;
	supportsReasoning: boolean;
	supportsReasoningEffort: boolean;
	// "" = unspecified. Only meaningful with supportsReasoning AND supportsReasoningEffort; the editor disables the
	// select otherwise and the save mapper drops the value, so a stale effort cannot survive unchecking the box.
	defaultReasoningEffort: ReasoningEffort | "";
}

export interface ExternalProviderFormValues {
	// The connection slug. Immutable once stored (it is part of every `ext:{connectionId}/{wireId}` model id), so the
	// editor only accepts input for it while creating.
	connectionId: string;
	displayName: string;
	baseUrl: string;
	locality: ExternalProviderLocality;
	// Write-only, like every other stored secret on this node: it loads blank, and a blank field on save means "keep
	// the stored key" — the request omits `apiKey` entirely. An empty string does NOT clear a key.
	apiKey: string;
	// The ONLY way back to a keyless connection: it maps to `clearApiKey: true` on the save request.
	clearApiKey: boolean;
	timeoutSeconds: string;
	models: ExternalProviderModelDraft[];
}

export type ExternalProviderFormErrors = Partial<Record<keyof ExternalProviderFormValues, string>>;

// The full canonical effort vocabulary the node's normalizer accepts. Unlike the chat composer — which narrows the set
// per model so it can never SEND a level the active model rejects — this is a declaration of what the operator's own
// server understands, so every recognized value is offered and the backend clamps it at the wire.
export const externalReasoningEfforts: readonly ReasoningEffort[] = ["none", "on", "minimal", "low", "medium", "high", "xhigh"];

// The connection slug grammar the backend stores ids under (D3): lowercase alphanumerics and hyphens, at most 32
// characters. Canonicalized once at write time, so the editor rejects anything else up front rather than letting a
// mixed-case id round-trip into a model id the name validator would refuse.
const CONNECTION_ID = /^[a-z0-9-]{1,32}$/;

const MAX_TIMEOUT_SECONDS = 600;

// Loopback, RFC 1918 private space, IPv4/IPv6 link-local, IPv6 unique-local, and the mDNS `.local` suffix — the set of
// addresses that can only be reached from this machine or this network segment. Declaring "Local" for anything else
// means prompts and workspace file contents leave the network, which is what the D1 warning says out loud.
function isPrivateIpv4(host: string): boolean {
	const octets = host.split(".");
	if (octets.length !== 4) {
		return false;
	}
	const parsed = octets.map((octet) => (/^\d{1,3}$/.test(octet) ? Number(octet) : Number.NaN));
	if (parsed.some((octet) => Number.isNaN(octet) || octet > 255)) {
		return false;
	}
	const first = parsed[0] ?? -1;
	const second = parsed[1] ?? -1;
	if (first === 10 || first === 127 || first === 0) {
		return true;
	}
	if (first === 192 && second === 168) {
		return true;
	}
	if (first === 172 && second >= 16 && second <= 31) {
		return true;
	}
	// 169.254.0.0/16 — IPv4 link-local (APIPA), reachable only on the local segment.
	return first === 169 && second === 254;
}

export function isTrustedLocalHost(host: string): boolean {
	// `URL.hostname` brackets an IPv6 literal; strip them before matching the address itself.
	const normalized = host.toLowerCase().replace(/^\[|\]$/g, "");
	if (normalized.length === 0) {
		return false;
	}
	if (normalized === "localhost" || normalized.endsWith(".localhost") || normalized.endsWith(".local")) {
		return true;
	}
	if (normalized === "::1" || normalized === "::") {
		return true;
	}
	// fc00::/7 — IPv6 unique-local; fe80::/10 — IPv6 link-local.
	if (normalized.startsWith("fc") || normalized.startsWith("fd") || normalized.startsWith("fe8")) {
		return true;
	}
	return isPrivateIpv4(normalized);
}

// The base URL's hostname (lower-cased), or null when the value is not a parseable URL.
function baseUrlHost(baseUrl: string): string | null {
	try {
		return new URL(baseUrl.trim()).hostname.toLowerCase();
	} catch {
		return null;
	}
}

// Drives the D1 warning: the operator declared full local trust for an address this node cannot reach privately.
// An unparseable URL does not warn — the base-URL error already tells that story, and two errors about one field
// read as noise.
export function shouldWarnLocalDeclaration(values: ExternalProviderFormValues): boolean {
	if (values.locality !== "Local") {
		return false;
	}
	const host = baseUrlHost(values.baseUrl);
	return host !== null && !isTrustedLocalHost(host);
}

// Shape-only URL pre-check. The backend normalizer owns the canonical form (a `/v1`-terminated base) and every
// remaining rule; this exists so an obviously wrong address is caught before a round trip.
function isHttpAbsoluteUrl(value: string): boolean {
	let url: URL;
	try {
		url = new URL(value);
	} catch {
		return false;
	}
	if (url.protocol !== "http:" && url.protocol !== "https:") {
		return false;
	}
	// Userinfo and fragments are refused by the backend normalizer; say so here rather than after a failed save.
	return url.username.length === 0 && url.password.length === 0 && url.hash.length === 0 && url.hostname.length > 0;
}

// Blank is valid (unspecified); otherwise a positive whole number within the given ceiling.
function isOptionalPositiveInteger(value: string, max: number): boolean {
	const trimmed = value.trim();
	if (trimmed.length === 0) {
		return true;
	}
	if (!/^\d+$/.test(trimmed)) {
		return false;
	}
	const parsed = Number(trimmed);
	return parsed > 0 && parsed <= max;
}

const MAX_CONTEXT_LENGTH = 10_000_000;

function validateModelDrafts(models: readonly ExternalProviderModelDraft[]): string | undefined {
	const seen = new Set<string>();
	for (const model of models) {
		const wireId = model.wireId.trim();
		if (wireId.length === 0) {
			continue;
		}
		// The map the backend writes these ids into is case-insensitive, so two rows differing only in case would
		// collide there rather than here.
		const key = wireId.toLowerCase();
		if (seen.has(key)) {
			return `Model "${wireId}" is listed twice. Each backing model id may be registered once per connection.`;
		}
		seen.add(key);

		if (!isOptionalPositiveInteger(model.contextLength, MAX_CONTEXT_LENGTH)) {
			return `Context length for "${wireId}" must be a whole number of tokens, or blank when the server's window is unknown.`;
		}
	}
	return undefined;
}

/**
 * Validates the connection editor. Required fields, the connection-slug grammar, and an obviously malformed address
 * are checked here so the operator sees them inline; every deeper bound (name lengths, model and connection caps, the
 * wire-id grammar, the reasoning-effort vocabulary) is owned by the encrypted store and surfaces as the 400's own
 * message, so it is deliberately not restated.
 *
 * `isNew` is passed rather than derived: a stored connection's id is immutable and its field is read-only, so it must
 * not be re-validated against input the operator can no longer correct.
 */
export function validateExternalProviderForm(values: ExternalProviderFormValues, isNew: boolean): ExternalProviderFormErrors {
	const errors: ExternalProviderFormErrors = {};

	if (isNew) {
		const connectionId = values.connectionId.trim();
		if (connectionId.length === 0) {
			errors.connectionId = "Enter an id for this connection, e.g. unsloth-box.";
		} else if (!CONNECTION_ID.test(connectionId)) {
			errors.connectionId = "The id may use lowercase letters, digits and hyphens only, up to 32 characters.";
		}
	}

	if (values.displayName.trim().length === 0) {
		errors.displayName = "Enter a name for this connection.";
	}

	if (!isHttpAbsoluteUrl(values.baseUrl.trim())) {
		errors.baseUrl = "Enter an absolute http(s) base URL, e.g. http://127.0.0.1:8080/v1.";
	}

	if (!isOptionalPositiveInteger(values.timeoutSeconds, MAX_TIMEOUT_SECONDS)) {
		errors.timeoutSeconds = `Enter a timeout between 1 and ${MAX_TIMEOUT_SECONDS} seconds, or leave it blank for the default.`;
	}

	const modelsError = validateModelDrafts(values.models);
	if (modelsError !== undefined) {
		errors.models = modelsError;
	}

	return errors;
}
