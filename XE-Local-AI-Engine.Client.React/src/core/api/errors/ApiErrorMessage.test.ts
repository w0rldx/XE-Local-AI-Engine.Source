import { describe, expect, it, vi } from "vitest";

// No test i18n instance; resolve inline defaults.
vi.mock("i18next", () => ({ t: (key: string, fallback?: string) => fallback ?? key }));

import { ApiError } from "@/core/api/errors/ApiError";
import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { NetworkError } from "@/core/api/errors/NetworkError";

describe("apiErrorMessage", () => {
	it("surfaces a blocked-build 409 reason carried on an ApiError", () => {
		const error = new ApiError(409, {
			reason: "processes-running",
			message: "Stop or eject all running llama.cpp models before building the runtime.",
		} as never);

		expect(apiErrorMessage(error, "fallback")).toBe(
			"Stop or eject all running llama.cpp models before building the runtime.",
		);
	});

	it("prefers ProblemDetails detail on a normal API failure", () => {
		const error = new ApiError(400, { type: "about:blank", title: "Bad Request", status: 400, detail: "Invalid backend" });

		expect(apiErrorMessage(error, "fallback")).toBe("Invalid backend");
	});

	it("reads a raw axios body for the 401/429 path that bypasses the ProblemDetails interceptor", () => {
		const error = { response: { data: { message: "Too many requests." } } };

		expect(apiErrorMessage(error, "fallback")).toBe("Too many requests.");
	});

	it("uses a plain Error's message", () => {
		expect(apiErrorMessage(new Error("Network error"), "fallback")).toBe("Network error");
	});

	// A request that never reached the node used to arrive as `new Error("Network error")`, so every page rendered that
	// untranslated two-word literal and no next action. The interceptor now throws a typed, message-less NetworkError
	// and the sentence is resolved — localized — here.
	it("answers a NetworkError with the node-unreachable sentence, not the caller's fallback", () => {
		expect(apiErrorMessage(new NetworkError(), "Could not load Development projects.")).toBe(
			"Can't reach the node. Check that it's running and try again.",
		);
	});

	it("falls back when the failure carries no message at all", () => {
		expect(apiErrorMessage(new ApiError(500, undefined as never), "fallback")).toBe("fallback");
		expect(apiErrorMessage(null, "fallback")).toBe("fallback");
		expect(apiErrorMessage({ response: { data: {} } }, "fallback")).toBe("fallback");
	});
});
