// Pure, unit-testable redaction helpers (plan §3, §10).
//
// "Redact at capture, not at export": these run BEFORE anything enters the breadcrumb buffer, so
// secrets/PII (Bearer tokens in headers AND URLs, password fields, chat/message/agent bodies,
// console-arg objects) never persist in cleartext. All functions are pure and idempotent —
// redacting already-redacted data is a no-op.

import type { Breadcrumb, NetworkEntry, NetworkTransport } from "@/core/diagnostics/Types";

export const REDACTED = "[REDACTED]";

/** Header / object keys whose values are always masked (lowercased comparison). */
const SENSITIVE_KEYS: ReadonlySet<string> = new Set([
	"authorization",
	"auth",
	"password",
	"passwd",
	"pwd",
	"token",
	"access_token",
	"accesstoken",
	"refresh_token",
	"refreshtoken",
	"id_token",
	"idtoken",
	"secret",
	"client_secret",
	"clientsecret",
	"apikey",
	"api_key",
	"x-api-key",
	"cookie",
	"set-cookie",
	"bearer",
]);

/** URL query-parameter names whose values are stripped. */
const SENSITIVE_QUERY_PARAMS: ReadonlySet<string> = new Set([
	"token",
	"access_token",
	"accesstoken",
	"refresh_token",
	"id_token",
	"bearer",
	"auth",
	"authorization",
	"apikey",
	"api_key",
	"key",
	"code",
	"secret",
]);

/** URL fragments that mark a request body as PII-bearing (chat/message/agent surfaces). */
const SENSITIVE_BODY_URL_FRAGMENTS: readonly string[] = ["chat", "message", "agent"];

const BEARER_PATTERN = /Bearer\s+[A-Za-z0-9._~+/=-]+/gi;

function isSensitiveKey(key: string): boolean {
	return SENSITIVE_KEYS.has(key.toLowerCase());
}

/** Mask any `Bearer <token>` substring inside a free-text string. */
export function redactString(value: string): string {
	return value.replace(BEARER_PATTERN, `Bearer ${REDACTED}`);
}

/** Strip `Authorization`/`Bearer`-style header values regardless of casing. */
export function redactHeaders(headers: Readonly<Record<string, unknown>>): Record<string, unknown> {
	const result: Record<string, unknown> = {};
	for (const [key, value] of Object.entries(headers)) {
		result[key] = isSensitiveKey(key) ? REDACTED : redactValue(value);
	}
	return result;
}

/** Remove token-bearing query parameters from a URL while preserving everything else. */
export function redactUrl(url: string): string {
	try {
		// Relative URLs need a base to parse; the base host is discarded from the output below.
		const base = "http://redacted.local";
		const parsed = new URL(url, base);
		let mutated = false;
		for (const name of [...parsed.searchParams.keys()]) {
			if (SENSITIVE_QUERY_PARAMS.has(name.toLowerCase())) {
				parsed.searchParams.set(name, REDACTED);
				mutated = true;
			}
		}
		if (!mutated) {
			return url;
		}
		// Re-emit in the original absolute/relative form.
		const isAbsolute = /^[a-z][a-z0-9+.-]*:\/\//i.test(url);
		return isAbsolute ? parsed.toString() : `${parsed.pathname}${parsed.search}${parsed.hash}`;
	} catch {
		// Fall back to the Bearer-string scrub if the URL is unparseable.
		return redactString(url);
	}
}

/** True when a URL's request/response body must be dropped (chat/message/agent endpoints). */
export function isSensitiveBodyUrl(url: string): boolean {
	const lower = url.toLowerCase();
	return SENSITIVE_BODY_URL_FRAGMENTS.some((fragment) => lower.includes(fragment));
}

/** Deep-redact an arbitrary value: mask sensitive keys, scrub Bearer strings, guard cycles. */
export function redactValue(value: unknown, seen: WeakSet<object> = new WeakSet(), depth = 0): unknown {
	if (typeof value === "string") {
		return redactString(value);
	}
	if (value === null || typeof value !== "object") {
		return value;
	}
	if (depth > 6 || seen.has(value)) {
		return "[Truncated]";
	}
	seen.add(value);

	if (Array.isArray(value)) {
		return value.map((item) => redactValue(item, seen, depth + 1));
	}

	const result: Record<string, unknown> = {};
	for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
		result[key] = isSensitiveKey(key) ? REDACTED : redactValue(child, seen, depth + 1);
	}
	return result;
}

/** Redact an array of console arguments (objects deep-redacted, strings scrubbed). */
export function redactConsoleArgs(args: readonly unknown[]): unknown[] {
	return args.map((arg) => redactValue(arg));
}

/**
 * A raw network observation a collector produces before it is reduced to the persisted
 * {@link NetworkEntry}. Bodies/headers MAY be present here and are always dropped.
 */
export interface RawNetworkObservation {
	readonly transport: NetworkTransport;
	readonly method: string;
	readonly url: string;
	readonly status?: number;
	readonly durationMs?: number;
	readonly traceId?: string;
	readonly requestBody?: unknown;
	readonly responseBody?: unknown;
	readonly requestHeaders?: Readonly<Record<string, unknown>>;
}

/**
 * Reduce a raw observation to a clean {@link NetworkEntry}: bodies are always dropped (the contract
 * has no body field) and the URL's token query params are stripped. This satisfies plan §10's
 * "keep method/url/status/traceId only" for sensitive endpoints — and is stricter for all others.
 */
export function toNetworkEntry(raw: RawNetworkObservation): NetworkEntry {
	return {
		transport: raw.transport,
		method: raw.method.toUpperCase(),
		url: redactUrl(raw.url),
		...(raw.status === undefined ? {} : { status: raw.status }),
		...(raw.durationMs === undefined ? {} : { durationMs: Math.round(raw.durationMs) }),
		...(raw.traceId === undefined ? {} : { traceId: raw.traceId }),
	};
}

/**
 * Defensive, idempotent redaction of a fully-formed breadcrumb. The buffer runs this on every
 * `push` so the "no secrets in the buffer" invariant holds even if a collector forgets.
 */
export function redactBreadcrumb(crumb: Breadcrumb): Breadcrumb {
	switch (crumb.category) {
		case "network":
			return { ...crumb, entry: { ...crumb.entry, url: redactUrl(crumb.entry.url) } };
		case "console":
			return {
				...crumb,
				message: redactString(crumb.message),
				...(crumb.args === undefined ? {} : { args: redactConsoleArgs(crumb.args) }),
			};
		case "navigation":
			return { ...crumb, to: redactUrl(crumb.to), ...(crumb.from === undefined ? {} : { from: redactUrl(crumb.from) }) };
		case "state":
			return {
				...crumb,
				diff: crumb.diff.map((field) => ({
					key: field.key,
					from: isSensitiveKey(field.key) ? REDACTED : redactValue(field.from),
					to: isSensitiveKey(field.key) ? REDACTED : redactValue(field.to),
				})),
			};
		case "lifecycle":
			return {
				...crumb,
				message: redactString(crumb.message),
				...(crumb.data === undefined ? {} : { data: redactValue(crumb.data) as Record<string, unknown> }),
			};
		case "error":
			// Error message/stack/componentStack are free text captured verbatim from thrown errors, so a
			// leaked Bearer token in an Authorization header echoed into an error string would otherwise
			// persist cleartext in IndexedDB. redactString is idempotent, so re-redacting is a no-op.
			return {
				...crumb,
				error: {
					...crumb.error,
					message: redactString(crumb.error.message),
					...(crumb.error.stack === undefined ? {} : { stack: redactString(crumb.error.stack) }),
					...(crumb.error.componentStack === undefined
						? {}
						: { componentStack: redactString(crumb.error.componentStack) }),
				},
			};
		default:
			return crumb;
	}
}
