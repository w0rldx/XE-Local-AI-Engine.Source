// @vitest-environment jsdom

import { http, HttpResponse } from "msw";
import { beforeEach, describe, expect, it } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { probeLocalApi } from "@/features/api-foundation/api/ValidationProblemProbeApi";
import { domainErrorRoute, localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";

// This suite deliberately does NOT mock the generated SDK. Mocking it leaves nothing under test but the argument
// object the wrapper builds; going over the wire through MSW exercises what actually breaks in production — the
// shared axios instance and its baseURL, the auth request interceptor, and the ProblemDetails response interceptor
// that produces every ApiError the UI renders.

describe("probeLocalApi over the real client", () => {
	beforeEach(() => {
		useNodeAuthStore.getState().actions.clear();
	});

	it("posts the body to the generated route and returns the parsed response", async () => {
		let observedBody: unknown;
		let observedContentType: string | null = null;
		server.use(
			http.post(localApiPath("diagnostics/validation-probe"), async ({ request }) => {
				observedBody = await request.json();
				observedContentType = request.headers.get("content-type");
				return HttpResponse.json({ name: "operator" });
			}),
		);

		await expect(probeLocalApi("operator")).resolves.toEqual({ name: "operator" });
		expect(observedBody).toEqual({ name: "operator" });
		expect(observedContentType).toContain("application/json");
	});

	it("sends the stored access token as a bearer header", async () => {
		useNodeAuthStore.getState().actions.setToken({
			accessToken: "token-abc",
			expiresAtUtc: new Date(Date.now() + 60_000).toISOString(),
		});
		let authorization: string | null = null;
		server.use(
			http.post(localApiPath("diagnostics/validation-probe"), ({ request }) => {
				authorization = request.headers.get("authorization");
				return HttpResponse.json({ name: "operator" });
			}),
		);

		await probeLocalApi("operator");

		expect(authorization).toBe("Bearer token-abc");
	});

	// The interceptor casts every non-2xx body to ProblemDetails and throws ApiError; `detail` is the operator-facing
	// sentence. Asserted end-to-end rather than by constructing an ApiError, because the cast is the part that broke.
	it("maps a ProblemDetails 400 to an ApiError carrying the server detail", async () => {
		server.use(
			problemDetailsRoute("post", "diagnostics/validation-probe", 400, {
				title: "Bad Request",
				detail: "Name must not be empty.",
			}),
		);

		const error = await probeLocalApi("x").catch((thrown: unknown) => thrown);

		expect(error).toBeInstanceOf(ApiError);
		expect((error as ApiError).statusCode).toBe(400);
		expect((error as ApiError).message).toBe("Name must not be empty.");
	});

	// Regression guard for the EMPTY-TOAST bug (agent-knowledge §5 "Error surfacing"): endpoints that answer with a
	// typed domain body instead of ProblemDetails have no `detail`, so ApiError.message was `undefined` and
	// `toast.error(undefined)` rendered a blank notification with the real reason discarded. ApiError now resolves
	// detail → message → title, so the operator still sees the server's own sentence.
	it("still surfaces a message when a 409 answers with a typed domain body instead of ProblemDetails", async () => {
		server.use(
			domainErrorRoute("post", "diagnostics/validation-probe", 409, {
				reason: "BuildInProgress",
				message: "A source build is already running.",
			}),
		);

		const error = await probeLocalApi("operator").catch((thrown: unknown) => thrown);

		expect(error).toBeInstanceOf(ApiError);
		expect((error as ApiError).message).toBe("A source build is already running.");
	});
});
