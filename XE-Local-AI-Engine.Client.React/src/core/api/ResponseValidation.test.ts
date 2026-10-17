import { describe, expect, it } from "vitest";
import { z } from "zod";

import { ApiError } from "@/core/api/errors/ApiError";
import { isResponseValidationError } from "@/core/api/ResponseValidation";

function makeZodError(): z.ZodError {
	const result = z.object({ name: z.string() }).safeParse({ name: 12345 });
	if (result.success) {
		throw new Error("expected the parse to fail");
	}

	return result.error;
}

describe("isResponseValidationError", () => {
	it("is true for the ZodError a generated response validator throws", () => {
		expect(isResponseValidationError(makeZodError())).toBe(true);
	});

	it("is false for a server HTTP ApiError, even a 422", () => {
		const serverError = new ApiError(422, { type: "about:blank", title: "Unprocessable", status: 422, detail: "bad" });

		expect(isResponseValidationError(serverError)).toBe(false);
	});

	it("is false for a plain Error", () => {
		expect(isResponseValidationError(new Error("nope"))).toBe(false);
	});
});
