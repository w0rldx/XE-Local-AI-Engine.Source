import { AxiosError } from "axios";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ZodError } from "zod";

import { ApiError } from "@/core/api/errors/ApiError";
import { NetworkError } from "@/core/api/errors/NetworkError";
import { getErrorStatus, isTransientError, retryTransient, shouldRetryQuery } from "@/core/api/errors/RetryClassification";
import type { ProblemDetails } from "@/core/api/models/ProblemDetails";

// Builds an ApiError the way the ProblemDetails interceptor does for any non-2xx status except 401/429.
function apiError(status: number): ApiError {
	return new ApiError(status, { detail: `status ${status}` } as ProblemDetails);
}

// 401 and 429 are NOT converted to ApiError by the interceptors — they stay raw AxiosErrors.
function axiosStatusError(status: number): AxiosError {
	return new AxiosError("http error", AxiosError.ERR_BAD_REQUEST, undefined, undefined, {
		data: undefined,
		status,
		statusText: "",
		headers: {},
		config: {} as never,
	});
}

// The ProblemDetails interceptor rethrows an Axios ERR_NETWORK as a typed NetworkError whose message is
// deliberately EMPTY, so the classifier has to recognize the type. Matching on a message string instead made a
// briefly-unreachable node (restarting, port moved, machine waking) fail on the first attempt with no retry.
const networkError = new NetworkError();
const axiosTransportError = new AxiosError("Network Error", "ERR_NETWORK");

// Mirrors TanStack Query's retryer: it calls retry(failureCount, error) with a 0-based failureCount and retries while
// the predicate is true, so this returns the TOTAL number of attempts (1 initial try + N retries).
function totalAttempts(error: unknown): number {
	let failureCount = 0;
	let attempts = 1;
	while (shouldRetryQuery(failureCount, error)) {
		attempts += 1;
		failureCount += 1;
	}
	return attempts;
}

describe("getErrorStatus", () => {
	it("reads statusCode from a normalized ApiError", () => {
		expect(getErrorStatus(apiError(404))).toBe(404);
	});

	it("reads response.status from a raw AxiosError", () => {
		expect(getErrorStatus(axiosStatusError(429))).toBe(429);
	});

	it("answers 422 for a response-shape mismatch, so a contract failure is attempted once", () => {
		const zodError = new ZodError([]);

		expect(getErrorStatus(zodError)).toBe(422);
		expect(totalAttempts(zodError)).toBe(1);
	});

	it("returns undefined for a transport error with no response", () => {
		expect(getErrorStatus(networkError)).toBeUndefined();
		expect(getErrorStatus(axiosTransportError)).toBeUndefined();
	});
});

describe("isTransientError", () => {
	it.each([408, 429, 500, 502, 503, 504])("treats %i as transient", (status) => {
		expect(isTransientError(apiError(status))).toBe(true);
	});

	it.each([400, 401, 403, 404, 409, 422])("treats %i as terminal", (status) => {
		expect(isTransientError(apiError(status))).toBe(false);
	});

	it("treats network / transport interruptions as transient", () => {
		expect(isTransientError(networkError)).toBe(true);
		expect(isTransientError(axiosTransportError)).toBe(true);
	});

	// Pins the property the classifier must not depend on: NetworkError carries NO message, because the copy an
	// operator sees is localized at render time. Any classifier that matches a message string instead of the type
	// silently reports every transport interruption terminal, and shouldRetryQuery then gives up after one attempt.
	it("classifies a NetworkError by type, not by its (empty) message", () => {
		expect(networkError.message).toBe("");
		expect(isTransientError(networkError)).toBe(true);
	});

	it("treats an unclassifiable error as terminal", () => {
		expect(isTransientError(new Error("boom"))).toBe(false);
		expect(isTransientError({ nope: true })).toBe(false);
	});
});

describe("shouldRetryQuery attempt counts", () => {
	// Deterministic 4xx never retry: exactly one attempt.
	it.each([400, 403, 404])("makes exactly 1 attempt for a deterministic %i", (status) => {
		expect(totalAttempts(apiError(status))).toBe(1);
	});

	// Transient HTTP statuses retry up to the bound: 3 retries → 4 total attempts.
	it("makes 4 attempts (3 retries) for 408", () => {
		expect(totalAttempts(apiError(408))).toBe(4);
	});

	it("makes 4 attempts (3 retries) for 429", () => {
		expect(totalAttempts(axiosStatusError(429))).toBe(4);
	});

	it("makes 4 attempts (3 retries) for 500", () => {
		expect(totalAttempts(apiError(500))).toBe(4);
	});

	it("makes 4 attempts (3 retries) for a network error", () => {
		expect(totalAttempts(networkError)).toBe(4);
		expect(totalAttempts(axiosTransportError)).toBe(4);
	});
});

// The vault pre-host frees the port ~2.6 s before the real host listens; a route guard's status read must ride
// that gap out instead of landing on the error boundary.
describe("retryTransient", () => {
	beforeEach(() => {
		vi.useFakeTimers();
	});

	afterEach(() => {
		vi.useRealTimers();
	});

	it("resolves once a network error is followed by an answer", async () => {
		const operation = vi.fn().mockRejectedValueOnce(axiosTransportError).mockResolvedValue("status");

		const result = retryTransient(operation);
		await vi.advanceTimersByTimeAsync(250);

		await expect(result).resolves.toBe("status");
		expect(operation).toHaveBeenCalledTimes(2);
	});

	it("rejects with the last error once a persistent network failure outlasts the budget", async () => {
		const operation = vi.fn().mockRejectedValue(axiosTransportError);
		let settled = false;

		const result = retryTransient(operation).finally(() => {
			settled = true;
		});
		const rejected = expect(result).rejects.toBe(axiosTransportError);
		await vi.advanceTimersByTimeAsync(9_000);
		expect(settled).toBe(false);

		await vi.advanceTimersByTimeAsync(1_000);
		await rejected;
		// 250, 500, 1000, 2000, 2000, 2000, 2000 ms waits: eight attempts inside the 10 s budget.
		expect(operation).toHaveBeenCalledTimes(8);
	});

	it("rethrows a deterministic 4xx at once", async () => {
		const operation = vi.fn().mockRejectedValue(apiError(404));

		await expect(retryTransient(operation)).rejects.toBeInstanceOf(ApiError);
		expect(operation).toHaveBeenCalledTimes(1);
	});
});
