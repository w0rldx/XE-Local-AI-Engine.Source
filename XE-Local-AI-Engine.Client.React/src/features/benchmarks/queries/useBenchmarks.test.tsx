// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import {
	useBenchmarkProject,
	useBenchmarkProjects,
	useBenchmarkRuns,
	useEligibleBenchmarkModels,
	useStartBenchmarkRun,
	useUpdateBenchmarkProject,
} from "@/features/benchmarks/queries/useBenchmarks";
import { domainErrorRoute, jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

// These hooks call the generated SDK functions imperatively (not through the TanStack `*Options()` wrappers) because
// several derive their poll cadence from already-mapped domain data. Serving the routes over MSW keeps the generated
// request/response validators and the shared interceptor chain in the test, and makes the optimistic-concurrency
// contract observable: every mutation carries an expectedVersion, and a 409 must reach the caller as an ApiError
// with the server's own sentence.

const projectId = "aaaaaaaa-0000-4000-8000-000000000001";
const runId = "bbbbbbbb-0000-4000-8000-000000000002";

function projectRow(overrides: Record<string, unknown> = {}) {
	return {
		id: projectId,
		name: "Summarisation",
		contextTokens: 4096,
		agentDefinitionId: "cccccccc-0000-4000-8000-000000000003",
		judgeEnabled: false,
		runCount: 1,
		isFrozen: true,
		version: 2,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		...overrides,
	};
}

function projectDetail(overrides: Record<string, unknown> = {}) {
	return { ...projectRow(), coreTask: "Summarise the attached text.", ...overrides };
}

function runRow(overrides: Record<string, unknown> = {}) {
	return {
		id: runId,
		projectId,
		primaryModelName: "model.gguf",
		modelContentFingerprint: "v1:test",
		agentName: "Summariser",
		agentVersion: 1,
		requestedContextTokens: 4096,
		primaryStatus: "Succeeded",
		judgeStatus: "Skipped",
		version: 3,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		...overrides,
	};
}

const draft = {
	name: "Summarisation",
	coreTask: "Summarise the attached text.",
	contextTokens: 4096,
	agentDefinitionId: "cccccccc-0000-4000-8000-000000000003",
	judgeEnabled: false,
	judgeModelName: null,
	judgeContextTokens: null,
	judgePromptVersion: 1,
	judgeOutputSchemaVersion: 1,
};

describe("benchmark queries over the real client", () => {
	it("maps the project list through the boundary mapper", async () => {
		server.use(jsonRoute("get", "benchmarks/projects", { items: [projectRow()] }));
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useBenchmarkProjects(), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(result.current.data?.[0]).toMatchObject({ id: projectId, name: "Summarisation", isFrozen: true, runCount: 1 });
	});

	it("returns an empty list when the node reports no projects", async () => {
		server.use(jsonRoute("get", "benchmarks/projects", {}));
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useBenchmarkProjects(), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(result.current.data).toEqual([]);
	});

	// Project and run reads are addressed by id and must stay off the wire until one exists.
	it("does not read a project or its runs without a project id", async () => {
		let reads = 0;
		server.use(
			http.get(localApiPath(`benchmarks/projects/${projectId}`), () => {
				reads += 1;
				return HttpResponse.json(projectDetail());
			}),
			http.get(localApiPath(`benchmarks/projects/${projectId}/runs`), () => {
				reads += 1;
				return HttpResponse.json({ items: [runRow()] });
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result, rerender } = renderHook(
			({ id }: { id: string | null }) => ({ project: useBenchmarkProject(id), runs: useBenchmarkRuns(id) }),
			{ wrapper, initialProps: { id: null as string | null } },
		);

		expect(result.current.project.fetchStatus).toBe("idle");
		expect(result.current.runs.fetchStatus).toBe("idle");
		expect(reads).toBe(0);

		rerender({ id: projectId });

		await waitFor(() => expect(result.current.project.isSuccess).toBe(true));
		await waitFor(() => expect(result.current.runs.isSuccess).toBe(true));
		expect(result.current.project.data?.coreTask).toBe("Summarise the attached text.");
		expect(result.current.runs.data?.[0]).toMatchObject({ id: runId, primaryStatus: "Succeeded", judgeStatus: "Skipped" });
	});

	// The run list is paged on the wire even though the UI shows one page.
	it("requests the runs page explicitly", async () => {
		let observedUrl = "";
		server.use(
			http.get(localApiPath(`benchmarks/projects/${projectId}/runs`), ({ request }) => {
				observedUrl = request.url;
				return HttpResponse.json({ items: [] });
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useBenchmarkRuns(projectId), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(observedUrl).toContain("page=1");
		expect(observedUrl).toContain("pageSize=100");
	});

	// The eligible-model read is context-aware: a requested context narrows the candidates server-side.
	it("passes the requested context tokens to the eligible-model read and omits them when unset", async () => {
		const urls: string[] = [];
		server.use(
			http.get(localApiPath("benchmarks/eligible-models"), ({ request }) => {
				urls.push(request.url);
				return HttpResponse.json({ items: [{ modelName: "m.gguf", modelContentFingerprint: "v1:abc" }] });
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => ({ scoped: useEligibleBenchmarkModels(8192), all: useEligibleBenchmarkModels() }), {
			wrapper,
		});

		await waitFor(() => expect(result.current.scoped.isSuccess).toBe(true));
		await waitFor(() => expect(result.current.all.isSuccess).toBe(true));
		expect(urls.some((url) => url.includes("contextTokens=8192"))).toBe(true);
		expect(urls.some((url) => !url.includes("contextTokens"))).toBe(true);
		expect(result.current.all.data?.[0]).toMatchObject({ modelName: "m.gguf", supportsTools: false, origin: null });
	});

	// Optimistic concurrency: the caller's expected version rides in the body, and the refreshed project must land in
	// the already-mounted project/list caches without a manual refetch.
	it("update sends the expected version and refreshes the project caches", async () => {
		let observedBody: unknown;
		let detail = projectDetail();
		server.use(
			http.get(localApiPath("benchmarks/projects"), () => HttpResponse.json({ items: [projectRow(detail)] })),
			http.get(localApiPath(`benchmarks/projects/${projectId}`), () => HttpResponse.json(detail)),
			http.put(localApiPath(`benchmarks/projects/${projectId}`), async ({ request }) => {
				observedBody = await request.json();
				detail = projectDetail({ name: "Renamed", version: 3 });
				return HttpResponse.json(detail);
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(
			() => ({ project: useBenchmarkProject(projectId), update: useUpdateBenchmarkProject() }),
			{ wrapper },
		);
		await waitFor(() => expect(result.current.project.isSuccess).toBe(true));

		result.current.update.mutate({ projectId, expectedVersion: 2, draft });

		await waitFor(() => expect(result.current.update.isSuccess).toBe(true));
		expect(observedBody).toMatchObject({ name: "Summarisation", expectedVersion: 2 });
		await waitFor(() => expect(result.current.project.data?.name).toBe("Renamed"));
	});

	it("start run posts the model and the expected project version", async () => {
		let observedBody: unknown;
		server.use(
			http.post(localApiPath(`benchmarks/projects/${projectId}/runs`), async ({ request }) => {
				observedBody = await request.json();
				return HttpResponse.json(runRow({ primaryStatus: "Queued", judgeStatus: "Pending" }));
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useStartBenchmarkRun(), { wrapper });

		result.current.mutate({ projectId, modelName: "model.gguf", expectedProjectVersion: 2 });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(observedBody).toEqual({ modelName: "model.gguf", expectedProjectVersion: 2 });
		expect(result.current.data).toMatchObject({ id: runId, primaryStatus: "Queued" });
	});

	// A stale expectedVersion is the expected failure of the concurrency contract; the operator has to be told why.
	it("surfaces a version-conflict 409 as an ApiError carrying the server message", async () => {
		server.use(
			domainErrorRoute("post", `benchmarks/projects/${projectId}/runs`, 409, {
				reason: "VersionConflict",
				message: "The project changed since it was loaded.",
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useStartBenchmarkRun(), { wrapper });

		result.current.mutate({ projectId, modelName: "model.gguf", expectedProjectVersion: 1 });

		await waitFor(() => expect(result.current.isError).toBe(true));
		expect(result.current.error).toBeInstanceOf(ApiError);
		expect((result.current.error as ApiError).statusCode).toBe(409);
		expect((result.current.error as ApiError).message).toBe("The project changed since it was loaded.");
	});
});
