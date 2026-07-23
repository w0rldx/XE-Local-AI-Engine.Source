import { ApiError } from "@/core/api/errors/ApiError";

// Reads a message off a raw axios-shaped rejection (`error.response.data`). Almost every HTTP failure is converted to
// an ApiError by the shared ProblemDetails interceptor before it reaches a caller, but 401 and 429 are deliberately
// re-rejected as the original AxiosError, so this shape still has to be handled.
function axiosResponseMessage(error: unknown): string | null {
	if (error === null || typeof error !== "object" || !("response" in error)) {
		return null;
	}

	const response = (error as { response?: unknown }).response;
	if (response === null || typeof response !== "object" || !("data" in response)) {
		return null;
	}

	const data = (response as { data?: unknown }).data;
	if (data === null || typeof data !== "object") {
		return null;
	}

	for (const key of ["detail", "message", "title"] as const) {
		const value = (data as Record<string, unknown>)[key];
		if (typeof value === "string" && value.trim().length > 0) {
			return value;
		}
	}

	return null;
}

/**
 * Resolves the most specific operator-facing message from an unknown thrown API failure, falling back to a supplied
 * (localized) string when the server authored none.
 *
 * Order: the server's own text (an {@link ApiError}'s resolved `message` — RFC 9457 `detail`, a typed domain body's
 * `message`, or `title`), then a raw axios body for the interceptor-exempt 401/429 path, then the error's own message,
 * then the fallback. Only server-authored strings are echoed — never a raw response payload.
 */
export function apiErrorMessage(error: unknown, fallback: string): string {
	if (error instanceof ApiError && error.message.trim().length > 0) {
		return error.message;
	}

	const fromResponse = axiosResponseMessage(error);
	if (fromResponse !== null) {
		return fromResponse;
	}

	if (error instanceof Error && error.message.trim().length > 0) {
		return error.message;
	}

	return fallback;
}
