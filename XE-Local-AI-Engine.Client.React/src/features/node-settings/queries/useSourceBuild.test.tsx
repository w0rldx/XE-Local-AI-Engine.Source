// @vitest-environment jsdom

import { act, renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import { useStartSourceBuild } from "@/features/node-settings/queries/useLocalRuntime";
import { domainErrorRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

// Serving the endpoint over MSW instead of mocking the generated mutation factory means the assertion is on the JSON
// that actually leaves the browser — the shape the backend validates — rather than on the argument object handed to a
// stub. Everything between the hook and the wire (generated SDK request validator, shared axios instance, interceptor
// chain) stays in the test.

const sourceBuildPath = "model-fit/llamacpp/source-build";

const startedResponse = {
	started: true,
	status: {
		phase: "Building",
		isRunning: true,
		terminal: false,
		logStartSequence: 0,
		logLines: [],
	},
};

describe("useStartSourceBuild over the real client", () => {
	it("sends the exact normalized custom build body", async () => {
		let observedBody: unknown;
		server.use(
			http.post(localApiPath(sourceBuildPath), async ({ request }) => {
				observedBody = await request.json();
				return HttpResponse.json(startedResponse);
			}),
		);
		const { wrapper } = createProvidersWrapper();
		const { result } = renderHook(() => useStartSourceBuild(), { wrapper });

		await act(async () => {
			await result.current.mutateAsync({
				backend: "cuda",
				source: "custom",
				repository: " https://github.com/example/fork ",
				commit: "ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD",
				acknowledgeCustomSourceRisk: true,
			});
		});

		expect(observedBody).toEqual({
			backend: "cuda",
			source: "custom",
			repository: "https://github.com/example/fork",
			commit: "abcdefabcdefabcdefabcdefabcdefabcdefabcd",
			acknowledgeCustomSourceRisk: true,
		});
	});

	// An official-source build carries no repository and cannot acknowledge a custom-source risk, whatever the draft
	// happens to hold — the null/false are normalized on the way out, not left to the server to reject.
	it("nulls the repository and drops the acknowledgement for an official-source build", async () => {
		let observedBody: unknown;
		server.use(
			http.post(localApiPath(sourceBuildPath), async ({ request }) => {
				observedBody = await request.json();
				return HttpResponse.json(startedResponse);
			}),
		);
		const { wrapper } = createProvidersWrapper();
		const { result } = renderHook(() => useStartSourceBuild(), { wrapper });

		await act(async () => {
			await result.current.mutateAsync({
				backend: "cpu",
				source: "official",
				repository: " https://github.com/example/fork ",
				commit: "   ",
				acknowledgeCustomSourceRisk: true,
			});
		});

		expect(observedBody).toEqual({
			backend: "cpu",
			source: "official",
			repository: null,
			commit: null,
			acknowledgeCustomSourceRisk: false,
		});
	});

	// The source-build 409 is the endpoint that produced the empty-toast bug (agent-knowledge §5 "Error surfacing"):
	// it answers with a typed `{ reason, message }` body carrying no ProblemDetails `detail`. The operator must still
	// get the server's sentence, so the whole interceptor path is asserted here rather than on a synthetic ApiError.
	it("surfaces the typed 409 domain body as an ApiError message", async () => {
		server.use(
			domainErrorRoute("post", sourceBuildPath, 409, {
				reason: "BuildAlreadyRunning",
				message: "A llama.cpp source build is already running.",
			}),
		);
		const { wrapper } = createProvidersWrapper();
		const { result } = renderHook(() => useStartSourceBuild(), { wrapper });

		act(() => {
			result.current.mutate({
				backend: "cuda",
				source: "official",
				repository: "",
				commit: "",
				acknowledgeCustomSourceRisk: false,
			});
		});

		await waitFor(() => expect(result.current.isError).toBe(true));
		expect(result.current.error).toBeInstanceOf(ApiError);
		expect((result.current.error as ApiError).statusCode).toBe(409);
		expect((result.current.error as ApiError).message).toBe("A llama.cpp source build is already running.");
	});
});
