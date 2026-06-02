import { describe, expect, it } from "vitest";
import { z } from "zod";
import { ApiError } from "@/core/api/errors/ApiError";
import {
	RESPONSE_VALIDATION_PROBLEM_TITLE,
	isResponseValidationError,
	mapResponseValidationError,
	withResponseValidation,
} from "@/core/api/ResponseValidation";

function makeZodError(): z.ZodError {
	const result = z.object({ name: z.string() }).safeParse({ name: 12345 });
	if (result.success) {
		throw new Error("expected the parse to fail");
	}

	return result.error;
}

describe("mapResponseValidationError", () => {
	it("remaps a ZodError into a 422 ApiError with a generic, payload-free message", () => {
		const mapped = mapResponseValidationError(makeZodError());

		expect(mapped).toBeInstanceOf(ApiError);
		const apiError = mapped as ApiError;
		expect(apiError.statusCode).toBe(422);
		expect(apiError.apiProblemDetails.title).toBe(RESPONSE_VALIDATION_PROBLEM_TITLE);
		// Privacy: the received value (12345) must never appear in the surfaced message.
		expect(apiError.message).not.toContain("12345");
		expect(apiError.message).not.toContain("name");
	});

	it("passes a non-zod Error through unchanged", () => {
		const original = new Error("network down");

		expect(mapResponseValidationError(original)).toBe(original);
	});

	it("passes an already-mapped ApiError through unchanged", () => {
		const original = new ApiError(500, { type: "about:blank", title: "Server error", status: 500, detail: "boom" });

		expect(mapResponseValidationError(original)).toBe(original);
	});
});

describe("isResponseValidationError", () => {
	it("is true for a remapped response-validation error", () => {
		expect(isResponseValidationError(mapResponseValidationError(makeZodError()))).toBe(true);
	});

	it("is false for a server HTTP ApiError", () => {
		const serverError = new ApiError(500, { type: "about:blank", title: "Server error", status: 500, detail: "boom" });

		expect(isResponseValidationError(serverError)).toBe(false);
	});

	it("is false for a plain Error", () => {
		expect(isResponseValidationError(new Error("nope"))).toBe(false);
	});
});

describe("withResponseValidation", () => {
	it("remaps a ZodError thrown by queryFn while preserving queryKey", async () => {
		const queryKey = ["listScheduledJobs"] as const;
		const options = {
			queryKey,
			queryFn: async () => {
				throw makeZodError();
			},
		};

		const wrapped = withResponseValidation(options);

		expect(wrapped.queryKey).toBe(queryKey);
		await expect(wrapped.queryFn()).rejects.toBeInstanceOf(ApiError);
	});

	it("passes a successful queryFn result through", async () => {
		const wrapped = withResponseValidation({
			queryFn: async () => ({ ok: true }),
		});

		await expect(wrapped.queryFn()).resolves.toEqual({ ok: true });
	});

	it("remaps a ZodError thrown by mutationFn", async () => {
		const wrapped = withResponseValidation({
			mutationFn: async () => {
				throw makeZodError();
			},
		});

		await expect(wrapped.mutationFn()).rejects.toBeInstanceOf(ApiError);
	});

	it("leaves a non-zod queryFn rejection unchanged", async () => {
		const failure = new Error("network down");
		const wrapped = withResponseValidation({
			queryFn: async () => {
				throw failure;
			},
		});

		await expect(wrapped.queryFn()).rejects.toBe(failure);
	});
});
