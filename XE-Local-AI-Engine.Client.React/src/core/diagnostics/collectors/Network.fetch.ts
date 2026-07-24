// fetch collector. Wraps `globalThis.fetch` ONCE; covers SSE streams + bare fetch.
//
// Same-origin requests get a `traceparent` header injected; cross-origin requests are left untouched
// (a `traceparent` is NOT CORS-safelisted and would force a preflight). Every call is
// recorded as a redacted `{transport:'fetch'}` breadcrumb.

import { push } from "@/core/diagnostics/BreadcrumbBuffer";
import { toNetworkEntry } from "@/core/diagnostics/Redact";
import { extractTraceId, generateTraceparent } from "@/core/diagnostics/Trace";

let installed = false;

/** Wrap `globalThis.fetch` once. Returns a teardown that restores the original implementation. */
export function installFetchCollector(): () => void {
	if (installed) {
		return () => undefined;
	}
	const original = globalThis.fetch;
	if (typeof original !== "function") {
		return () => undefined;
	}
	installed = true;

	const wrapped: typeof fetch = (input, init) => {
		const url = resolveUrl(input);
		const method = resolveMethod(input, init);
		const sameOrigin = isSameOrigin(url);

		let traceId: string | undefined;
		let requestInit = init;
		if (sameOrigin) {
			const trace = generateTraceparent();
			traceId = trace.traceId;
			requestInit = withTraceHeader(input, init, trace.header);
		}

		const startedAt = nowMs();
		return original(input, requestInit).then(
			(response) => {
				recordFetch(method, url, response.status, traceId, response.headers, startedAt);
				return response;
			},
			(error: unknown) => {
				recordFetch(method, url, undefined, traceId, undefined, startedAt);
				throw error;
			},
		);
	};

	globalThis.fetch = wrapped;

	return () => {
		globalThis.fetch = original;
		installed = false;
	};
}

function withTraceHeader(input: RequestInfo | URL, init: RequestInit | undefined, header: string): RequestInit {
	const headers = new Headers(init?.headers ?? (input instanceof Request ? input.headers : undefined));
	headers.set("traceparent", header);
	return { ...init, headers };
}

function recordFetch(
	method: string,
	url: string,
	status: number | undefined,
	traceId: string | undefined,
	responseHeaders: Headers | undefined,
	startedAt: number,
): void {
	const serverTraceId = responseHeaders
		? extractTraceId(responseHeaders.get("traceresponse") ?? responseHeaders.get("x-trace-id"))
		: undefined;
	push({
		category: "network",
		entry: toNetworkEntry({
			transport: "fetch",
			method,
			url,
			durationMs: nowMs() - startedAt,
			...(status === undefined ? {} : { status }),
			...((serverTraceId ?? traceId) ? { traceId: serverTraceId ?? traceId } : {}),
		}),
	});
}

function resolveUrl(input: RequestInfo | URL): string {
	if (typeof input === "string") {
		return input;
	}
	if (input instanceof URL) {
		return input.toString();
	}
	return input.url;
}

function resolveMethod(input: RequestInfo | URL, init: RequestInit | undefined): string {
	if (init?.method) {
		return init.method;
	}
	if (input instanceof Request) {
		return input.method;
	}
	return "GET";
}

function isSameOrigin(url: string): boolean {
	const origin = globalThis.location?.origin;
	if (!origin) {
		return false;
	}
	try {
		return new URL(url, origin).origin === origin;
	} catch {
		return false;
	}
}

function nowMs(): number {
	return globalThis.performance?.now?.() ?? Date.now();
}
