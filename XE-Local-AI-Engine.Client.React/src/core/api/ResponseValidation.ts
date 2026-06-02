import { ZodError } from "zod";
import { ApiError } from "@/core/api/errors/ApiError";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";

/**
 * Title sentinel marking an {@link ApiError} that originated from hey-api zod response validation
 * (a response-shape / contract mismatch) rather than an HTTP error returned by the server.
 */
export const RESPONSE_VALIDATION_PROBLEM_TITLE = "Response validation failed";

const RESPONSE_VALIDATION_STATUS = 422;

const responseValidationProblemDetails: ProblemDetails = {
	type: "about:blank",
	title: RESPONSE_VALIDATION_PROBLEM_TITLE,
	status: RESPONSE_VALIDATION_STATUS,
	// Intentionally generic. The raw response payload and the ZodError issues are never echoed here:
	// they can carry server data that must not leak into logs or user-facing toasts (privacy).
	detail: "The server returned a response in an unexpected shape.",
};

/**
 * Remaps a thrown zod {@link ZodError} into this codebase's {@link ApiError} contract.
 *
 * Generated hey-api SDK calls run their zod response schema as the client `responseValidator`, which
 * throws a raw `ZodError` *after* the 2xx axios response resolves — so it bypasses the axios
 * ProblemDetails interceptor that normally produces `ApiError`. This remap restores a single error
 * contract for the UI. Non-zod errors (already-mapped `ApiError`, network `Error`, …) pass through
 * unchanged.
 */
export function mapResponseValidationError(error: unknown): unknown {
	if (error instanceof ZodError) {
		return new ApiError(RESPONSE_VALIDATION_STATUS, responseValidationProblemDetails);
	}

	return error;
}

/** Type guard distinguishing a response-validation (contract) failure from a server HTTP error. */
export function isResponseValidationError(error: unknown): error is ApiError {
	return (
		error instanceof ApiError &&
		error.statusCode === RESPONSE_VALIDATION_STATUS &&
		error.apiProblemDetails.title === RESPONSE_VALIDATION_PROBLEM_TITLE
	);
}

function wrapBoundaryFn(target: Record<string, unknown>, key: "queryFn" | "mutationFn"): void {
	const original = target[key];
	if (typeof original !== "function") {
		return;
	}

	const originalFn = original as (...args: unknown[]) => unknown;
	target[key] = async (...args: unknown[]) => {
		try {
			return await originalFn(...args);
		} catch (error) {
			throw mapResponseValidationError(error);
		}
	};
}

/**
 * Wraps a generated hey-api TanStack options object so a `ZodError` thrown by its `queryFn` /
 * `mutationFn` (response-shape validation) surfaces as an {@link ApiError} instead of a raw
 * `ZodError`. The generated `queryKey` and every other option are preserved untouched.
 *
 * Compose at each migrated feature hook — the single choke-point for the generated data layer:
 *   useQuery(withResponseValidation(listScheduledJobsOptions({ query })))
 *   useMutation(withResponseValidation(createScheduledJobMutation()))
 */
export function withResponseValidation<TOptions extends object>(options: TOptions): TOptions {
	const wrapped = { ...options } as TOptions & Record<string, unknown>;
	wrapBoundaryFn(wrapped, "queryFn");
	wrapBoundaryFn(wrapped, "mutationFn");
	return wrapped;
}
