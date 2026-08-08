import { isAxiosError } from "axios";

import { ApiError } from "@/core/api/errors/ApiError";
import { NetworkError } from "@/core/api/errors/NetworkError";

// How many times a transient failure is retried before the query is allowed to settle as an error.
// Total attempts = MAX_QUERY_RETRIES + 1 (the initial try plus the retries).
export const MAX_QUERY_RETRIES = 3;

// Extracts an HTTP status code from whatever shape an error reaches the query layer as. The axios
// response interceptors normalize most failures into an ApiError (carrying `statusCode`), but 401/429
// stay raw AxiosErrors (carrying `response.status`) and unexpected throws may be plain objects — so a
// single classifier has to read every shape rather than assume the Axios `response.status` field.
export function getErrorStatus(error: unknown): number | undefined {
	if (error instanceof ApiError) {
		return error.statusCode;
	}

	if (isAxiosError(error)) {
		return error.response?.status;
	}

	if (typeof error === "object" && error !== null) {
		const withResponse = error as { response?: { status?: unknown }; statusCode?: unknown };
		if (typeof withResponse.statusCode === "number") {
			return withResponse.statusCode;
		}
		if (typeof withResponse.response?.status === "number") {
			return withResponse.response.status;
		}
	}

	return undefined;
}

// True for a network/transport interruption (no HTTP response was ever received). The ProblemDetails
// interceptor rethrows an Axios `ERR_NETWORK` as a typed NetworkError, so the TYPE is what identifies that case
// here. It used to be matched on the literal message "Network error"; that string no longer exists anywhere in
// the app (NetworkError's message is deliberately empty so renderers fall through to a localized fallback), so
// matching on it would silently classify every transport interruption as terminal. Axios timeout/abort codes
// never carry a status either, so they are read straight off the AxiosError.
function isTransportError(error: unknown): boolean {
	if (error instanceof NetworkError) {
		return true;
	}

	if (isAxiosError(error)) {
		return error.code === "ERR_NETWORK" || error.code === "ECONNABORTED" || error.code === "ETIMEDOUT";
	}

	return false;
}

// Classifies whether an error is worth retrying. Only transient classes are retried — transport
// interruptions and the transient HTTP statuses (408 Request Timeout, 429 Too Many Requests, 5xx). Every
// deterministic 4xx (400/401/403/404/…) and every unclassifiable error is terminal: retrying it just
// replays a guaranteed failure N times and delays the error surfacing to the user.
export function isTransientError(error: unknown): boolean {
	if (isTransportError(error)) {
		return true;
	}

	const status = getErrorStatus(error);
	if (status === undefined) {
		return false;
	}

	if (status === 408 || status === 429) {
		return true;
	}

	return status >= 500 && status <= 599;
}

// The global TanStack Query retry predicate: retry a transient failure up to MAX_QUERY_RETRIES times.
export function shouldRetryQuery(failureCount: number, error: unknown): boolean {
	return failureCount < MAX_QUERY_RETRIES && isTransientError(error);
}

// Bounded exponential backoff (1s, 2s, 4s, … capped at 30s) so retried transient failures don't hammer
// the node. `attemptIndex` is 0-based on the first retry.
export function queryRetryDelay(attemptIndex: number): number {
	return Math.min(1000 * 2 ** attemptIndex, 30_000);
}
