import type { ProblemDetails } from "@/core/api/models/ProblemDetails";

// Resolves the operator-facing message the axios ProblemDetails interceptor should carry on the thrown ApiError.
//
// The interceptor casts EVERY non-2xx response body to ProblemDetails, but several endpoints answer with a typed
// domain body instead — e.g. the llama.cpp source-build and CUDA-build 409s return `{ reason, message }`, which has no
// `detail`. Reading `detail` alone left `message` undefined on those errors, and `toast.error(undefined)` rendered an
// empty notification with the real reason discarded. Prefer the RFC 9457 `detail`, then a domain body's `message`,
// then the ProblemDetails `title` (generic, e.g. "Conflict"), and finally an empty string so callers can apply their
// own localized fallback rather than showing an invented English string.
function resolveMessage(body: ProblemDetails | undefined): string {
	const candidates = body as unknown as Record<string, unknown> | undefined;
	for (const key of ["detail", "message", "title"] as const) {
		const value = candidates?.[key];
		if (typeof value === "string" && value.trim().length > 0) {
			return value;
		}
	}
	return "";
}

export class ApiError extends Error {
	constructor(statusCode: number, apiProblemDetails: ProblemDetails) {
		super();

		this.statusCode = statusCode;
		this.message = resolveMessage(apiProblemDetails);
		this.apiProblemDetails = apiProblemDetails;
	}

	statusCode: number;

	apiProblemDetails: ProblemDetails;
}
