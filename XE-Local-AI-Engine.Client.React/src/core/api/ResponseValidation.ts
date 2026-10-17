import { ZodError } from "zod";

/**
 * True for a response-shape (contract) mismatch: every generated SDK call runs its zod response schema as the client
 * `responseValidator`, which throws a raw `ZodError` after the 2xx response resolved, so the axios ProblemDetails
 * interceptor never sees it. The error sinks (`apiErrorMessage`, `getErrorStatus`, `describeError`) handle this type
 * themselves; its `message` lists issue paths and must never reach a toast or a breadcrumb.
 */
export function isResponseValidationError(error: unknown): error is ZodError {
	return error instanceof ZodError;
}
