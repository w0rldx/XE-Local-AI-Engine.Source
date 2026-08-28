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

// The node's own bounds (ExternalProviderStoreSchema.MinTimeoutSeconds / MaxTimeoutSeconds). Restated here only so the
// operator is told inline instead of by a 400; the store still owns the rule.
export const MIN_TIMEOUT_SECONDS = 5;
export const MAX_TIMEOUT_SECONDS = 3600;

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

// Canonical form of an IPv6 LITERAL, or null when the host is not one. The URL parser is the validator: it accepts
// exactly the IPv6 literal grammar inside brackets and canonicalizes what it accepts, so an ordinary DNS name that
// merely begins with hex-looking characters — `fd-api.example.com` — is rejected here instead of being mistaken for a
// unique-local address. Accepts the host either bracketed (as `URL.hostname` returns it) or bare (as it is typed).
function ipv6Literal(host: string): string | null {
	const bracketed = host.startsWith("[") && host.endsWith("]") ? host : `[${host}]`;
	let hostname: string;
	try {
		hostname = new URL(`http://${bracketed}/`).hostname;
	} catch {
		return null;
	}
	return hostname.startsWith("[") && hostname.endsWith("]") ? hostname.slice(1, -1) : null;
}

// fc00::/7 (unique-local), fe80::/10 (link-local) and the two loopback/unspecified literals — the IPv6 half of "only
// reachable from this machine or this network segment". The CIDR test runs on the FIRST 16-bit group of a parsed
// literal, never on a string prefix.
function isPrivateIpv6(host: string): boolean {
	const literal = ipv6Literal(host);
	if (literal === null) {
		return false;
	}
	if (literal === "::1" || literal === "::") {
		return true;
	}
	const firstGroup = literal.split(":")[0] ?? "";
	if (!/^[0-9a-f]{1,4}$/.test(firstGroup)) {
		// A literal starting with "::" (an IPv4-mapped or otherwise compressed-leading address) is in neither range.
		return false;
	}
	const value = Number.parseInt(firstGroup, 16);
	// fc00::/7 — the top 7 bits are 1111110. fe80::/10 — the top 10 bits are 1111111010.
	return (value & 0xfe00) === 0xfc00 || (value & 0xffc0) === 0xfe80;
}

export function isTrustedLocalHost(host: string): boolean {
	const normalized = host.trim().toLowerCase();
	if (normalized.length === 0) {
		return false;
	}
	// Loopback and mDNS NAMES. Every other name is an ordinary DNS name that can resolve anywhere, so only IP literals
	// are checked against the private ranges below — a hostname is never treated as an address.
	if (normalized === "localhost" || normalized.endsWith(".localhost") || normalized.endsWith(".local")) {
		return true;
	}
	if (isPrivateIpv6(normalized)) {
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

/**
 * The scheme+host+port a base URL points at, or null when it is not parseable.
 *
 * The ORIGIN, not the whole URL, is what a stored credential is bound to: `/v1` → `/openai/v1` on the same server is
 * still the same endpoint holding the same key, while a changed host or port is a different server that must never be
 * handed the old key. The backend draws the same line, for both the probe's stored-key fallback and the save.
 */
export function baseUrlOrigin(baseUrl: string): string | null {
	try {
		const origin = new URL(baseUrl.trim()).origin;
		return origin === "null" ? null : origin;
	} catch {
		return null;
	}
}

// What the editor knows about the connection as it is STORED right now — the address the key was issued for, and
// whether there is a key at all. Absent while creating.
export interface ExternalProviderStoredConnection {
	readonly baseUrl: string;
	readonly hasApiKey: boolean;
}

/**
 * True when the operator has moved a key-bearing connection to a different origin without saying what to do with the
 * key. The backend REJECTS that save (400) rather than silently forwarding the stored credential to a new endpoint;
 * this is the same rule stated inline, before the round trip.
 *
 * Typing a new key or asking for removal both answer the question, so either one clears it.
 */
export function requiresApiKeyReentry(
	values: ExternalProviderFormValues,
	stored: ExternalProviderStoredConnection | undefined,
): boolean {
	if (stored === undefined || !stored.hasApiKey || values.clearApiKey || values.apiKey.trim().length > 0) {
		return false;
	}
	const storedOrigin = baseUrlOrigin(stored.baseUrl);
	const draftOrigin = baseUrlOrigin(values.baseUrl);
	// An unparseable draft address is the base-URL error's story, not this one.
	return storedOrigin !== null && draftOrigin !== null && draftOrigin !== storedOrigin;
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

// Blank is valid (unspecified); otherwise a whole number inside the inclusive range.
function isOptionalIntegerInRange(value: string, min: number, max: number): boolean {
	const trimmed = value.trim();
	if (trimmed.length === 0) {
		return true;
	}
	if (!/^\d+$/.test(trimmed)) {
		return false;
	}
	const parsed = Number(trimmed);
	return parsed >= min && parsed <= max;
}

const MAX_CONTEXT_LENGTH = 10_000_000;

function validateModelDrafts(models: readonly ExternalProviderModelDraft[]): string | undefined {
	const seen = new Set<string>();
	for (const model of models) {
		const wireId = model.wireId.trim();
		if (wireId.length === 0) {
			continue;
		}
		// Exact, like the store's own Ordinal check: remote model ids ARE case-sensitive, so "Qwen/qwen3" and
		// "qwen/Qwen3" are two registrable ids and collapsing them here would make one of them unreachable.
		if (seen.has(wireId)) {
			return `Model "${wireId}" is listed twice. Each backing model id may be registered once per connection.`;
		}
		seen.add(wireId);

		if (!isOptionalIntegerInRange(model.contextLength, 1, MAX_CONTEXT_LENGTH)) {
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
 *
 * `stored` is what the server currently holds for this connection, and only the key-rebinding rule reads it: without
 * it there is no way to tell that the address on screen has left the origin the stored key was issued for.
 */
export function validateExternalProviderForm(
	values: ExternalProviderFormValues,
	isNew: boolean,
	stored?: ExternalProviderStoredConnection,
): ExternalProviderFormErrors {
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

	if (!isOptionalIntegerInRange(values.timeoutSeconds, MIN_TIMEOUT_SECONDS, MAX_TIMEOUT_SECONDS)) {
		errors.timeoutSeconds = `Enter a timeout between ${MIN_TIMEOUT_SECONDS} and ${MAX_TIMEOUT_SECONDS} seconds, or leave it blank for the default.`;
	}

	if (requiresApiKeyReentry(values, stored)) {
		errors.apiKey =
			"This connection's stored key was issued for a different address. Type the key for the new endpoint, or use Remove key to go keyless.";
	}

	const modelsError = validateModelDrafts(values.models);
	if (modelsError !== undefined) {
		errors.models = modelsError;
	}

	return errors;
}
