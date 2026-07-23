import { describe, expect, it } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";

describe("ApiError", () => {
	it("keeps status and problem details for API error handling", () => {
		const problemDetails = {
			type: "https://example.test/problem",
			title: "Validation failed",
			status: 400,
			detail: "Invalid email address",
		};

		const error = new ApiError(400, problemDetails);

		expect(error.statusCode).toBe(400);
		expect(error.message).toBe("Invalid email address");
		expect(error.apiProblemDetails).toEqual(problemDetails);
	});

	it("falls back to a typed domain body's message when the response is not ProblemDetails", () => {
		// The 409 the llama.cpp source-build endpoint returns is `{ reason, message }`, not ProblemDetails. Reading
		// `detail` alone made `message` undefined and the toast rendered empty with the real reason discarded.
		const blocked = { reason: "prerequisites", message: "The official source repository is selected by the server." };

		const error = new ApiError(409, blocked as never);

		expect(error.message).toBe("The official source repository is selected by the server.");
	});

	it("falls back to the title when neither detail nor message carries text", () => {
		const error = new ApiError(409, { type: "about:blank", title: "Conflict", status: 409, detail: "  " });

		expect(error.message).toBe("Conflict");
	});

	it("resolves to an empty message when the body carries no text, so callers can apply their own fallback", () => {
		const error = new ApiError(500, undefined as never);

		expect(error.message).toBe("");
	});
});
