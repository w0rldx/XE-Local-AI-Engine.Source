// W3C trace-context generation + config→traceId pairing.
//
// `generateTraceparent()` produces `00-<32hex traceId>-<16hex spanId>-01`, injected by all three
// network collectors. A module-level WeakMap pairs an axios request config object to its trace id
// so the RC2 401 retry (which re-sends the SAME config object — see Interceptors.ts `retriedRequests`
// WeakSet) reuses the original trace id and logs once. The WeakMap composes with that existing
// config-identity model and keeps trace ids out of the generated client's types (no `any`).

import { randomHex } from "@/core/diagnostics/Ids";

const TRACE_ID_HEX = 32;
const SPAN_ID_HEX = 16;
/** `01` = sampled flag. */
const TRACE_FLAGS = "01";
const TRACE_VERSION = "00";

export interface Traceparent {
	/** Full `traceparent` header value. */
	readonly header: string;
	/** The 32-hex trace id (the snapshot ↔ backend join key). */
	readonly traceId: string;
}

/** Generate a fresh W3C `traceparent` header + its trace id. */
export function generateTraceparent(): Traceparent {
	const traceId = randomHex(TRACE_ID_HEX);
	const spanId = randomHex(SPAN_ID_HEX);
	return {
		header: `${TRACE_VERSION}-${traceId}-${spanId}-${TRACE_FLAGS}`,
		traceId,
	};
}

/** Build a `traceparent` header from a known trace id (reuse path). */
export function traceparentFromTraceId(traceId: string): string {
	return `${TRACE_VERSION}-${traceId}-${randomHex(SPAN_ID_HEX)}-${TRACE_FLAGS}`;
}

/**
 * Parse the 32-hex trace id out of a `traceparent`/`traceresponse` header (or a bare `X-Trace-Id`).
 * Returns undefined when the value is not a recognizable W3C header.
 */
export function extractTraceId(headerValue: string | null | undefined): string | undefined {
	if (!headerValue) {
		return undefined;
	}
	const trimmed = headerValue.trim();
	if (/^[0-9a-f]{32}$/i.test(trimmed)) {
		return trimmed.toLowerCase();
	}
	const parts = trimmed.split("-");
	const candidate = parts[1];
	if (parts.length >= 3 && candidate && /^[0-9a-f]{32}$/i.test(candidate)) {
		return candidate.toLowerCase();
	}
	return undefined;
}

// Request-config identity preserves trace IDs independently from retry tracking.

const configTraceIds = new WeakMap<object, string>();

/** Stash the trace id on a request-config object (keyed by identity, never cloned). */
export function rememberTraceId(config: object, traceId: string): void {
	configTraceIds.set(config, traceId);
}

/** Read the trace id previously stashed for a config object, if any. */
export function getTraceId(config: object): string | undefined {
	return configTraceIds.get(config);
}

/**
 * Ensure a config has a trace id: returns the existing one (e.g. on a 401 retry that reuses the
 * config) or generates and stashes a new one. Returns both the header and id for the caller.
 */
export function ensureConfigTrace(config: object): Traceparent {
	const existing = configTraceIds.get(config);
	if (existing) {
		return { header: traceparentFromTraceId(existing), traceId: existing };
	}
	const generated = generateTraceparent();
	configTraceIds.set(config, generated.traceId);
	return generated;
}
