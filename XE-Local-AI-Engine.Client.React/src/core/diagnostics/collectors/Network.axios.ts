// Axios network collector. Registered as the LAST interceptor pair in
// AxiosInstance.ts (after the existing 4 registrars).
//
// Request side: inject `traceparent`, stash the trace id on the config via the trace.ts WeakMap.
// Response/error side: record `{transport:'axios',...,traceId}`, read `traceresponse`/`X-Trace-Id`.
//
// IDEMPOTENT: the RC2 401 path re-sends the SAME config object (Interceptors.ts `retriedRequests`
// WeakSet), so the request interceptor runs twice and two responses (401 then retry) fire for one
// logical request. We dedupe the log by config identity (never cloning the config) and reuse the
// stashed trace id, so one logical request yields one trace and one breadcrumb.

import type { AxiosError, AxiosInstance, InternalAxiosRequestConfig } from "axios";

import { push } from "@/core/diagnostics/BreadcrumbBuffer";
import { toNetworkEntry } from "@/core/diagnostics/Redact";
import { ensureConfigTrace, extractTraceId, getTraceId } from "@/core/diagnostics/Trace";

const TRACE_HEADER = "traceparent";

// Configs already logged — guards against the 401-retry double observation (config identity).
const loggedConfigs = new WeakSet<object>();

/** Register the diagnostics trace/network interceptor pair on the shared axios instance. */
export function addDiagnosticsNetworkInterceptor(axiosInstance: AxiosInstance): void {
	axiosInstance.interceptors.request.use((request) => {
		const { header } = ensureConfigTrace(request);
		request.headers.set(TRACE_HEADER, header);
		return request;
	});

	axiosInstance.interceptors.response.use(
		(response) => {
			recordAxios(response.config, response.status, response.headers);
			return response;
		},
		(error: AxiosError) => {
			const config = error.config;
			if (config) {
				recordAxios(config, error.response?.status, error.response?.headers);
			}
			return Promise.reject(error);
		},
	);
}

function recordAxios(config: InternalAxiosRequestConfig, status: number | undefined, responseHeaders: unknown): void {
	if (loggedConfigs.has(config)) {
		return;
	}
	loggedConfigs.add(config);

	const traceId = getTraceId(config);
	const serverTraceId = readResponseTraceId(responseHeaders);

	push({
		category: "network",
		entry: toNetworkEntry({
			transport: "axios",
			method: config.method ?? "GET",
			url: typeof config.url === "string" ? config.url : "",
			...(status === undefined ? {} : { status }),
			...((serverTraceId ?? traceId) ? { traceId: serverTraceId ?? traceId } : {}),
		}),
	});
}

function readResponseTraceId(responseHeaders: unknown): string | undefined {
	if (!responseHeaders || typeof responseHeaders !== "object") {
		return undefined;
	}
	const headers = responseHeaders as Record<string, unknown>;
	const raw = headers["traceresponse"] ?? headers["x-trace-id"] ?? headers["X-Trace-Id"];
	return typeof raw === "string" ? extractTraceId(raw) : undefined;
}
