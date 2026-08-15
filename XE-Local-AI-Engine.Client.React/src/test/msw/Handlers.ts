import { http, HttpResponse, type DefaultBodyType, type HttpHandler } from "msw";

import type { ProblemDetails } from "@/core/api/models/ProblemDetails";

type Method = "get" | "post" | "put" | "patch" | "delete";

/**
 * Absolute path of a node REST route. FastEndpoints' OpenAPI paths already carry the `/api/local/v1` prefix and
 * `Generated.runtime.ts` pins the axios baseURL to `""`, so the generated SDK requests exactly this same-origin path.
 * Getting the prefix wrong shows up as an unhandled-request error rather than a silent 404, but it is one literal
 * worth not retyping per handler.
 */
export function localApiPath(path: string): string {
	return `/api/local/v1/${path.replace(/^\//, "")}`;
}

/** A route answering 200 with `body` as JSON. */
export function jsonRoute(method: Method, path: string, body: DefaultBodyType): HttpHandler {
	return http[method](localApiPath(path), () => HttpResponse.json(body));
}

/**
 * A route answering `status` with an RFC 9457 ProblemDetails body — what the node's `UseProblemDetails` pipeline
 * emits for a 4xx/5xx. The shared axios interceptor turns this into an `ApiError` whose `message` is the `detail`.
 */
export function problemDetailsRoute(
	method: Method,
	path: string,
	status: number,
	problem: Partial<ProblemDetails>,
): HttpHandler {
	return http[method](localApiPath(path), () =>
		HttpResponse.json(
			{ type: "about:blank", title: "Error", status, detail: "", ...problem } satisfies ProblemDetails,
			{ status, headers: { "content-type": "application/problem+json" } },
		),
	);
}

/**
 * A route answering `status` with a NON-ProblemDetails typed domain body (the shape the source-build / CUDA-build
 * 409s use: `{ reason, message }`). Regression fixture for the empty-toast bug — the interceptor casts every error
 * body to ProblemDetails, so a body with no `detail` must still yield a non-empty `ApiError.message`.
 */
export function domainErrorRoute(method: Method, path: string, status: number, body: DefaultBodyType): HttpHandler {
	return http[method](localApiPath(path), () => HttpResponse.json(body, { status }));
}
