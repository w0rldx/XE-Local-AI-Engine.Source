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
});
