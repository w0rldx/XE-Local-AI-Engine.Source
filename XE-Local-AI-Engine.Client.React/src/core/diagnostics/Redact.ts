// Pure, unit-testable redaction helpers.
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

const BEARER_PATTERN = /Bearer\s+[A-Za-z0-9._~+/=-]+/gi;

// The remaining shapes mirror the server-side support-bundle scrubber, so a snapshot and the server logs it ships with
// are redacted alike. Each replacement is outside its own pattern's alphabet, which keeps redactString idempotent.
const TOKEN_PATTERNS: readonly RegExp[] = [
	// JWT: three base64url segments, the first one a JSON header ("eyJ" = `{"`).
	/\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/g,
	// Prefixed keys: OpenAI-style, GitHub, Hugging Face, AWS access key ids, Slack, Google, Azure storage.
	/\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|hf_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|xox[abprs]-[A-Za-z0-9-]{10,}|AIza[0-9A-Za-z_-]{35}|AccountKey=[A-Za-z0-9+/=]{20,})/g,
];

// `password=…`, `api_key: …`, `password="…"` and JSON `"password": "…"` keep the key, the separator and any quotes;
// only the value is masked. A quoted value runs to the SAME closing quote, so the other quote character may appear
// inside it (`password="it's"`). Mirrors the server's AssignedSecretRegex: a bare value cannot start with `[` and a
// quoted one may not start with the marker, so an already-masked value never matches again.
const ASSIGNMENT_PATTERN =
	/\b(api[_-]?key|secret|password|connectionstring|client_secret|access_token|refresh_token)(["']?\s*[=:]\s*)(?:(["'])(?!\[REDACTED\])(?:(?!\3)[^\r\n]){4,}\3|[^\s,;{}"'[]{4,})/gi;

// A 20+ char run of key-ish characters, masked only when isSecretShaped says so (mirrors the server's TokenRunRegex).
const TOKEN_RUN_PATTERN = /(?<![A-Za-z0-9+/=_-])[A-Za-z0-9+/=_-]{20,}(?![A-Za-z0-9+/=_-])/g;
const DENSE_SEGMENT_LENGTH = 16;
const MIN_SECRET_DIGITS = 4;

// Secret-shaped: a '-', '_', '/', '+' or '=' separated segment of 16+ chars with a letter and 4+ digits that is neither
// hex (trace id, SHA) nor a compact timestamp (digits with at most two T/Z, 20260913T134250067Z). Model names, EF
// migration ids (20260913134250_AddBenchmarkP2Discrimination), versions and timestamps stay readable.
function isSecretShaped(run: string): boolean {
	return run.split(/[-_/+=]/).some((segment) => {
		const digits = segment.replace(/\D/g, "").length;
		const letters = segment.length - digits;
		return (
			segment.length >= DENSE_SEGMENT_LENGTH &&
			letters > 0 &&
			digits >= MIN_SECRET_DIGITS &&
			!/^[0-9a-f]+$/i.test(segment) &&
			!(letters <= 2 && /^[0-9tz]+$/i.test(segment))
		);
	});
}

const EMAIL_PATTERN = /[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}/g;

// A user-profile prefix (`/home/<u>`, `/Users/<u>`, `C:\Users\<u>`) becomes `~`, keeping the separator that followed.
// The look-behind skips a URL path segment such as `example.com/home/...`. A user name may hold single interior spaces
// (`C:\Users\Jane Doe\AppData`) only when a separator follows, so trailing prose after `/home/jane` is not swallowed;
// mirrors the server's AbsolutePathSanitizer segment.
const HOME_PATH_PATTERN =
	/(?<![\w.~-])(?:\/home\/|\/Users\/|[A-Za-z]:[\\/]Users[\\/])(?:[^\\/\s"'`<>|:]+(?:\s[^\\/\s"'`<>|:]+)*(?=[\\/])|[^\\/\s"'`<>|:]+)/gi;

function isSensitiveKey(key: string): boolean {
	return SENSITIVE_KEYS.has(key.toLowerCase());
}

/** Mask bearer/JWT/API-key tokens and e-mail addresses, and shorten user-profile paths to `~`, in free text. */
export function redactString(value: string): string {
	let result = value.replace(BEARER_PATTERN, `Bearer ${REDACTED}`);
	for (const pattern of TOKEN_PATTERNS) {
		result = result.replace(pattern, REDACTED);
	}
	result = result.replace(ASSIGNMENT_PATTERN, `$1$2$3${REDACTED}$3`);
	result = result.replace(TOKEN_RUN_PATTERN, (run) => (isSecretShaped(run) ? REDACTED : run));
	return result.replace(EMAIL_PATTERN, "[redacted-email]").replace(HOME_PATH_PATTERN, "~");
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
		// Fall back to the free-text scrub if the URL is unparseable.
		return redactString(url);
	}
}

/** Deep-redact an arbitrary value: mask sensitive keys, scrub free-text strings, guard cycles. */
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
 * has no body field) and the URL's token query params are stripped. This keeps method/url/status/traceId
 * only for sensitive endpoints — and is stricter for all others.
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
					...(crumb.error.componentStack === undefined ? {} : { componentStack: redactString(crumb.error.componentStack) }),
				},
			};
		default:
			return crumb;
	}
}
