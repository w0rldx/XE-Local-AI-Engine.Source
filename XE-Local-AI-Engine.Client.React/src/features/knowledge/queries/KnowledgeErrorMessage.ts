import { ApiError } from "@/core/api/errors/ApiError";

// Resolves the most specific human-readable message from an unknown thrown error, preferring the server's
// ProblemDetails (detail → first field error → title) over a raw Error message, then falling back. Mirrors the
// chat-attachment surface so KB upload/mutation failures surface the same shaped feedback. Never echoes raw
// response payloads (privacy) — only the server-authored ProblemDetails strings.
export function knowledgeErrorMessage(error: unknown, fallback: string): string {
	if (error instanceof ApiError) {
		const problem = error.apiProblemDetails as unknown as Record<string, unknown> | undefined;
		const detail = problem?.["detail"];
		if (typeof detail === "string" && detail.trim().length > 0) {
			return detail;
		}
		// FastEndpoints validation failures surface the specific message under an `errors` map rather than `detail`.
		const errors = problem?.["errors"];
		if (errors && typeof errors === "object") {
			for (const value of Object.values(errors as Record<string, unknown>)) {
				if (Array.isArray(value) && typeof value[0] === "string") {
					return value[0];
				}
				if (typeof value === "string") {
					return value;
				}
			}
		}
		const title = problem?.["title"];
		if (typeof title === "string" && title.trim().length > 0) {
			return title;
		}
	}
	if (error instanceof Error && error.message.trim().length > 0) {
		return error.message;
	}
	return fallback;
}
