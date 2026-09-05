// @vitest-environment jsdom

import { act, renderHook, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { describe, expect, it, vi } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import type { BenchmarkProjectDraft } from "@/features/benchmarks/models/BenchmarkModels";
import {
	useBenchmarkComparisons,
	useBenchmarkProject,
	useBenchmarkProjects,
	useBenchmarkRun,
	useBenchmarkRuns,
	useClearBenchmarkRunScore,
	useEligibleBenchmarkModels,
	useRejudgeBenchmarkProject,
	useStartBenchmarkRun,
	useUpdateBenchmarkJudgePolicy,
	useUpdateBenchmarkProject,
	useUpdateBenchmarkProjectFidelity,
} from "@/features/benchmarks/queries/useBenchmarks";
import { domainErrorRoute, jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { createProvidersWrapper } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

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
	return { ...projectRow(), coreTask: "Summarise the attached text.", judge: { enabled: false }, ...overrides };
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
		judge: { state: "none", policyCurrent: false, executionCurrent: false },
		qualityScoreSource: "none",
		modelGroupKey: "v1:test",
		version: 3,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		...overrides,
	};
}

/** The hooks' own poll cadence, mirrored so a timing assertion says which interval it is waiting past. */
const activeComparisonPollMs = 2_000;

/** A fidelity row as the node sends it: `kldState` rides along even when nothing was measured against a base. */
const fidelityRow = (status: string) => ({ status, kldState: "none" });

const draft: BenchmarkProjectDraft = {
	name: "Summarisation",
	coreTask: "Summarise the attached text.",
	contextTokens: 4096,
	maxOutputTokens: null,
	reasoningBudgetTokens: null,
	invocationTimeoutSeconds: null,
	agentDefinitionId: "cccccccc-0000-4000-8000-000000000003",
	judgeEnabled: false,
	judgeMode: "pointwise",
	judgeModelName: null,
	judgeContextTokens: null,
	rubric: null,
	referenceAnswer: null,
	fidelityEnabled: false,
	fidelityKldEnabled: false,
	fidelityChunks: null,
	fidelityKldBaseModelName: null,
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
				return HttpResponse.json({ items: [runRow()], rankCohort: { rankedCount: 0, totalScored: 0 } });
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
		expect(result.current.runs.data?.items[0]).toMatchObject({ id: runId, primaryStatus: "Succeeded" });
		expect(result.current.runs.data?.items[0]?.judge.state).toBe("none");
	});

	// The run list is paged on the wire, and the first page is what the table opens with.
	it("requests the runs page explicitly", async () => {
		let observedUrl = "";
		server.use(
			http.get(localApiPath(`benchmarks/projects/${projectId}/runs`), ({ request }) => {
				observedUrl = request.url;
				return HttpResponse.json({ items: [], rankCohort: { rankedCount: 0, totalScored: 0 } });
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useBenchmarkRuns(projectId), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(observedUrl).toContain("page=1");
		expect(observedUrl).toContain("pageSize=200");
	});

	// A matrix launch can create more runs than one page holds, and the node REFUSES a pageSize above 200 with a 400
	// (`ListBenchmarkRunsEndpoint`). So the pages are appended rather than one page grown — safe because the ranking is
	// project-wide and the pages are contiguous slices of that one order. The 400 route is the regression guard: a
	// hook that grew the page instead would fail its third read on any project with more than 400 runs.
	it("appends further pages instead of asking for a page the node refuses", async () => {
		const totalCount = 450;
		const observed: string[] = [];
		server.use(
			http.get(localApiPath(`benchmarks/projects/${projectId}/runs`), ({ request }) => {
				const params = new URL(request.url).searchParams;
				const page = Number(params.get("page") ?? 1);
				const pageSize = Number(params.get("pageSize") ?? 0);
				observed.push(`${page}/${pageSize}`);
				if (pageSize > 200) {
					return HttpResponse.json({ errors: { pageSize: ["pageSize must be between 1 and 200."] } }, { status: 400 });
				}
				const offset = (page - 1) * pageSize;
				return HttpResponse.json({
					items: Array.from({ length: Math.max(0, Math.min(pageSize, totalCount - offset)) }, (_, index) =>
						runRow({ id: `bbbbbbbb-0000-4000-8000-${String(offset + index).padStart(12, "0")}` }),
					),
					page,
					pageSize,
					totalCount,
					rankCohort: { rankedCount: 0, totalScored: 0 },
				});
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useBenchmarkRuns(projectId), { wrapper });

		await waitFor(() => expect(result.current.data?.items).toHaveLength(200));
		expect(result.current.data?.totalCount).toBe(totalCount);

		act(() => {
			result.current.loadMore();
		});
		await waitFor(() => expect(result.current.data?.items).toHaveLength(400));

		act(() => {
			result.current.loadMore();
		});
		await waitFor(() => expect(result.current.data?.items).toHaveLength(450));

		expect(observed).toEqual(["1/200", "2/200", "3/200"]);
		expect(result.current.isError).toBe(false);
		// Every id survives the concatenation: an appended page must not re-serve rows the previous one already had.
		expect(new Set(result.current.data?.items.map((run) => run.id)).size).toBe(450);
	});

	// The store pages by OFFSET over a newest-first order, and the poll refetches every loaded page. A run started
	// while two pages are loaded shifts every row down one, so page 2 re-serves the row that just left page 1. Without
	// the dedupe that is a repeated React key and one genuine run hidden behind its own copy.
	it("keeps one row per run when a new run shifts the pages apart", async () => {
		const id = (index: number) => `bbbbbbbb-0000-4000-8000-${String(index).padStart(12, "0")}`;
		server.use(
			http.get(localApiPath(`benchmarks/projects/${projectId}/runs`), ({ request }) => {
				const page = Number(new URL(request.url).searchParams.get("page") ?? 1);
				// Page 2 starts at the LAST id of page 1: exactly the one-row overlap a concurrent launch produces.
				const ids = page === 1 ? [id(1), id(2)] : [id(2), id(3)];
				return HttpResponse.json({
					items: ids.map((runId) => runRow({ id: runId })),
					page,
					pageSize: 2,
					totalCount: 4,
					rankCohort: { rankedCount: 0, totalScored: 0 },
				});
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useBenchmarkRuns(projectId), { wrapper });

		await waitFor(() => expect(result.current.data?.items).toHaveLength(2));

		act(() => {
			result.current.loadMore();
		});

		await waitFor(() => expect(result.current.data?.items).toHaveLength(3));
		const ids = result.current.data?.items.map((run) => run.id) ?? [];
		expect(new Set(ids).size).toBe(ids.length);
		expect(ids).toEqual([id(1), id(2), id(3)]);
	});

	it("stops offering more once every run is loaded", async () => {
		server.use(
			http.get(localApiPath(`benchmarks/projects/${projectId}/runs`), () =>
				HttpResponse.json({ items: [runRow()], page: 1, pageSize: 200, totalCount: 1, rankCohort: { rankedCount: 0, totalScored: 0 } }),
			),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useBenchmarkRuns(projectId), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(result.current.hasNextPage).toBe(false);
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
				return HttpResponse.json(runRow({ primaryStatus: "Queued" }));
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useStartBenchmarkRun(), { wrapper });

		result.current.mutate({ projectId, modelName: "model.gguf", expectedProjectVersion: 2, kvCacheType: null, repeatMode: "Throughput", answerVarianceTemperature: null });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		// The mode always rides along; the temperature never does in throughput mode, which the node samples at 0.
		expect(observedBody).toEqual({ modelName: "model.gguf", expectedProjectVersion: 2, repeatMode: "Throughput" });
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

		result.current.mutate({ projectId, modelName: "model.gguf", expectedProjectVersion: 1, kvCacheType: null, repeatMode: "Throughput", answerVarianceTemperature: null });

		await waitFor(() => expect(result.current.isError).toBe(true));
		expect(result.current.error).toBeInstanceOf(ApiError);
		expect((result.current.error as ApiError).statusCode).toBe(409);
		expect((result.current.error as ApiError).message).toBe("The project changed since it was loaded.");
	});
	// "Auto" is the absence of a pick, not a value: the node resolves the KV cache type at freeze, so the member must be
	// omitted rather than sent as a string the contract does not define. An explicit pick rides along verbatim.
	it("omits kvCacheType for Auto and sends an explicit pick verbatim", async () => {
		const bodies: unknown[] = [];
		server.use(
			http.post(localApiPath(`benchmarks/projects/${projectId}/runs`), async ({ request }) => {
				bodies.push(await request.json());
				return HttpResponse.json(runRow({ primaryStatus: "Queued" }));
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useStartBenchmarkRun(), { wrapper });

		result.current.mutate({ projectId, modelName: "model.gguf", expectedProjectVersion: 2, kvCacheType: null, repeatMode: "Throughput", answerVarianceTemperature: null });
		await waitFor(() => expect(bodies).toHaveLength(1));
		result.current.mutate({ projectId, modelName: "model.gguf", expectedProjectVersion: 2, kvCacheType: "q8_0", repeatMode: "Throughput", answerVarianceTemperature: null });
		await waitFor(() => expect(bodies).toHaveLength(2));

		expect(bodies[0]).toEqual({ modelName: "model.gguf", expectedProjectVersion: 2, repeatMode: "Throughput" });
		expect(bodies[1]).toEqual({ modelName: "model.gguf", expectedProjectVersion: 2, kvCacheType: "q8_0", repeatMode: "Throughput" });
	});

	// A 422 is the node refusing this KV type on this runtime; its sanitized reason has to survive to the caller.
	it("surfaces an unsupported KV cache type as a 422 ApiError carrying the server message", async () => {
		server.use(
			domainErrorRoute("post", `benchmarks/projects/${projectId}/runs`, 422, {
				reason: "UnsupportedKvCacheType",
				message: "q4_0 is not supported by the selected runtime.",
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useStartBenchmarkRun(), { wrapper });

		result.current.mutate({ projectId, modelName: "model.gguf", expectedProjectVersion: 2, kvCacheType: "q4_0", repeatMode: "Throughput", answerVarianceTemperature: null });

		await waitFor(() => expect(result.current.isError).toBe(true));
		expect((result.current.error as ApiError).statusCode).toBe(422);
		expect((result.current.error as ApiError).message).toBe("q4_0 is not supported by the selected runtime.");
	});
	// The cohort line rides alongside the rows: the UI cannot say "n of m ranked" from the rows alone, because runs the
	// node excluded are still in the list.
	it("returns the rank cohort alongside the mapped rows", async () => {
		server.use(
			jsonRoute("get", `benchmarks/projects/${projectId}/runs`, {
				items: [runRow({ rank: 1, qualityScore: 80, qualityScoreSource: "judge" })],
				rankCohort: { policyRevision: 2, executionKey: "key", cohortGeneration: 1, rankedCount: 1, totalScored: 3 },
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useBenchmarkRuns(projectId), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(result.current.data?.cohort).toMatchObject({ policyRevision: 2, rankedCount: 1, totalScored: 3 });
		expect(result.current.data?.items[0]).toMatchObject({ rank: 1, qualityScore: 80 });
	});

	// Clearing an override is a DELETE with the run version, never a PUT of 0.
	it("clears an operator score through DELETE with the expected version", async () => {
		let observedBody: unknown;
		server.use(
			http.delete(localApiPath(`benchmarks/runs/${runId}/score`), async ({ request }) => {
				observedBody = await request.json();
				return HttpResponse.json(runRow({ userScore: null, version: 4 }));
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useClearBenchmarkRunScore(), { wrapper });

		result.current.mutate({ id: runId, projectId, version: 3 });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(observedBody).toEqual({ expectedVersion: 3 });
	});

	// The judge policy is its own write with its own confirmation flag, and the 409 that asks for that confirmation has
	// to reach the caller with the node's `code` so the UI can raise the right dialog rather than a generic toast.
	it("sends the judge policy with its confirmation flag and surfaces the RejudgeRequired 409", async () => {
		const bodies: unknown[] = [];
		server.use(
			http.put(localApiPath(`benchmarks/projects/${projectId}/judge`), async ({ request }) => {
				bodies.push(await request.json());
				if (bodies.length === 1) {
					return HttpResponse.json(
						{ type: "about:blank", title: "Conflict", status: 409, detail: "Confirm the re-judge.", code: "RejudgeRequired" },
						{ status: 409, headers: { "content-type": "application/problem+json" } },
					);
				}
				return HttpResponse.json({ project: projectDetail({ version: 3 }), enqueuedRunIds: [runId] });
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useUpdateBenchmarkJudgePolicy(), { wrapper });
		const policy = { modelName: "judge.gguf", contextTokens: 8192, rubric: null, referenceAnswer: null };

		result.current.mutate({ projectId, expectedVersion: 2, policy, confirmRejudge: false });
		await waitFor(() => expect(result.current.isError).toBe(true));
		expect((result.current.error as ApiError).statusCode).toBe(409);
		expect((result.current.error as ApiError).apiProblemDetails).toMatchObject({ code: "RejudgeRequired" });

		result.current.mutate({ projectId, expectedVersion: 2, policy, confirmRejudge: true });
		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(bodies[0]).toEqual({ policy, expectedVersion: 2, confirmRejudge: false });
		expect(bodies[1]).toEqual({ policy, expectedVersion: 2, confirmRejudge: true });
		expect(result.current.data?.enqueuedRunCount).toBe(1);
	});

	// A run's detail query polls only while that run is active, so after a project-wide re-judge its open pane would
	// otherwise sit on the finished previous judging forever: nothing schedules another read of a run believed idle.
	it("refetches the detail of every run a project re-judge enqueued", async () => {
		let runReads = 0;
		server.use(
			http.get(localApiPath(`benchmarks/runs/${runId}`), () => {
				runReads += 1;
				return HttpResponse.json(runRow({ primaryStatus: "Succeeded" }));
			}),
			http.post(localApiPath(`benchmarks/projects/${projectId}/rejudge`), () =>
				HttpResponse.json({ project: projectDetail({ version: 3 }), enqueuedRunIds: [runId] }),
			),
		);
		const { wrapper } = createProvidersWrapper();

		const detail = renderHook(() => useBenchmarkRun(runId), { wrapper });
		await waitFor(() => expect(detail.result.current.isSuccess).toBe(true));
		expect(runReads).toBe(1);

		const rejudge = renderHook(() => useRejudgeBenchmarkProject(), { wrapper });
		rejudge.result.current.mutate({ projectId, expectedVersion: 2 });

		await waitFor(() => expect(runReads).toBeGreaterThan(1));
	});

	// Fidelity is measured on its OWN queue and only starts once the run itself is terminal, so a poll predicate reading
	// just the primary and the judge goes quiet the instant a measurement is queued — and the numbers it queued for never
	// arrive. Both runs below are terminal on primary and judge and differ ONLY in their fidelity row, so one elapsed
	// clock separates the two behaviours: whatever kept the first one reading cannot have been the run's own state.
	it("keeps polling a run whose fidelity measurement is in flight and leaves a measured one alone", async () => {
		const measuredRunId = "bbbbbbbb-0000-4000-8000-000000000009";
		const reads = { queued: 0, measured: 0 };
		server.use(
			http.get(localApiPath(`benchmarks/runs/${runId}`), () => {
				reads.queued += 1;
				return HttpResponse.json(runRow({ fidelity: fidelityRow("queued") }));
			}),
			http.get(localApiPath(`benchmarks/runs/${measuredRunId}`), () => {
				reads.measured += 1;
				return HttpResponse.json(runRow({ id: measuredRunId, fidelity: { ...fidelityRow("succeeded"), perplexityMean: 7.5 } }));
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => ({ queued: useBenchmarkRun(runId), measured: useBenchmarkRun(measuredRunId) }), {
			wrapper,
		});
		await waitFor(() => expect(result.current.measured.isSuccess).toBe(true));

		await waitFor(() => expect(reads.queued).toBeGreaterThan(1), { timeout: 8_000 });
		expect(result.current.queued.data?.fidelity?.status).toBe("queued");
		// Same wall clock, same terminal primary and judge: a settled measurement must not have brought a second read.
		expect(reads.measured).toBe(1);
	});

	// The ranked table reads the same rows, and it is where the operator watches a batch of measurements finish.
	it("keeps polling the runs list while a row's fidelity measurement is in flight", async () => {
		const quietProjectId = "aaaaaaaa-0000-4000-8000-000000000008";
		const reads = { active: 0, quiet: 0 };
		const page = (status: string) => ({
			items: [runRow({ fidelity: fidelityRow(status) })],
			rankCohort: { rankedCount: 0, totalScored: 0 },
		});
		server.use(
			http.get(localApiPath(`benchmarks/projects/${projectId}/runs`), () => {
				reads.active += 1;
				return HttpResponse.json(page("running"));
			}),
			http.get(localApiPath(`benchmarks/projects/${quietProjectId}/runs`), () => {
				reads.quiet += 1;
				return HttpResponse.json(page("skipped"));
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => ({ active: useBenchmarkRuns(projectId), quiet: useBenchmarkRuns(quietProjectId) }), {
			wrapper,
		});
		await waitFor(() => expect(result.current.quiet.isSuccess).toBe(true));

		await waitFor(() => expect(reads.active).toBeGreaterThan(1), { timeout: 8_000 });
		// `skipped` is a terminal answer, not a pending one: nothing more is coming, so nothing should keep asking.
		expect(reads.quiet).toBe(1);
	});

	// Enabling fidelity with `measureExisting` queues a measurement per existing run, but the response only COUNTS them.
	// Every affected run is terminal, so its own detail query stopped polling long ago and nothing else would tell it to
	// look again — the refreshed rows are what put both the list and the pane back on the poll.
	it("refreshes the runs list and the open run details when existing runs are queued for measurement", async () => {
		const reads = { detail: 0, list: 0 };
		let enqueuedCount = 2;
		server.use(
			http.get(localApiPath(`benchmarks/runs/${runId}`), () => {
				reads.detail += 1;
				return HttpResponse.json(runRow());
			}),
			http.get(localApiPath(`benchmarks/projects/${projectId}/runs`), () => {
				reads.list += 1;
				return HttpResponse.json({ items: [runRow()], rankCohort: { rankedCount: 0, totalScored: 0 } });
			}),
			http.patch(localApiPath(`benchmarks/projects/${projectId}/fidelity`), () =>
				HttpResponse.json({ project: projectDetail({ version: 3, fidelityEnabled: true }), enqueuedCount }),
			),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(
			() => ({
				detail: useBenchmarkRun(runId),
				runs: useBenchmarkRuns(projectId),
				save: useUpdateBenchmarkProjectFidelity(),
			}),
			{ wrapper },
		);
		await waitFor(() => expect(result.current.detail.isSuccess).toBe(true));
		await waitFor(() => expect(result.current.runs.isSuccess).toBe(true));
		expect(reads.detail).toBe(1);

		const fidelityDraft = {
			fidelityEnabled: true,
			fidelityKldEnabled: false,
			fidelityChunks: null,
			fidelityKldBaseModelName: null,
		};
		result.current.save.mutate({ projectId, expectedVersion: 2, draft: fidelityDraft, measureExisting: true });

		await waitFor(() => expect(reads.list).toBeGreaterThan(1));
		await waitFor(() => expect(reads.detail).toBeGreaterThan(1));

		// A save that measured nothing must NOT sweep every open pane: the list still refreshes (the project changed),
		// and the detail read count staying put is what says the family-wide invalidation was gated on the count.
		enqueuedCount = 0;
		const detailReadsAfterQueue = reads.detail;
		const listReadsAfterQueue = reads.list;
		result.current.save.mutate({ projectId, expectedVersion: 3, draft: fidelityDraft, measureExisting: true });

		await waitFor(() => expect(reads.list).toBeGreaterThan(listReadsAfterQueue));
		expect(reads.detail).toBe(detailReadsAfterQueue);
	});

	// A pairwise project's scores and ranks are read out of the FIT, and the comparisons poll is the only thing watching
	// for one: the runs list polls on RUN activity, which a pairwise judging leaves untouched, so the ranked table would
	// keep showing null scores against the very verdicts that produced them. The stages below are served one per poll,
	// so the cohort advances exactly the way it does on the node — a pair at a time, the fit only after the last one.
	it("refreshes the ranked runs when a pairwise fit arrives, and not while the verdicts merely progress", async () => {
		const comparison = (id: string, status: string, verdict: string | null) => ({
			id,
			runAId: runId,
			runBId: "bbbbbbbb-0000-4000-8000-000000000007",
			order: 0,
			status,
			verdict,
		});
		const pending = comparison("cccccccc-0000-4000-8000-00000000000c", "Running", null);
		const judged = comparison("cccccccc-0000-4000-8000-00000000000c", "Succeeded", "a");
		const second = comparison("dddddddd-0000-4000-8000-00000000000d", "Running", null);
		const fit = {
			fitKey: "fit-1",
			judgeExecutionKey: "exec-1",
			comparisonSetVersion: 1,
			cohortGeneration: 1,
			isCurrent: true,
			fittedSetJson: "[]",
			scores: [{ runId, score: 72, ciLow: 61, ciHigh: 83 }],
		};
		const cohort = { cohortGeneration: 1, comparisonSetVersion: 1 };
		const stages = [
			{ ...cohort, items: [pending, second], fit: null },
			// One verdict further along, same cohort and same comparison set: progress, not a new reading.
			{ ...cohort, items: [judged, second], fit: null },
			{ ...cohort, items: [judged, { ...second, status: "Succeeded", verdict: "b" }], fit },
		];
		let comparisonReads = 0;
		let listReads = 0;
		server.use(
			http.get(localApiPath(`benchmarks/projects/${projectId}/comparisons`), () => {
				comparisonReads += 1;
				return HttpResponse.json(stages[Math.min(comparisonReads - 1, stages.length - 1)]);
			}),
			http.get(localApiPath(`benchmarks/projects/${projectId}/runs`), () => {
				listReads += 1;
				return HttpResponse.json({ items: [runRow()], rankCohort: { rankedCount: 0, totalScored: 0 } });
			}),
		);
		const { wrapper } = createProvidersWrapper();

		// Fake timers own the poll cadence for this test alone. The stages have to arrive one per poll, and the last
		// assertion is a NEGATIVE one — that a disarmed poll fires nothing more — which a real wait can only ever answer
		// for the 2.5 s the box happened to give it. `vi.advanceTimersByTimeAsync` makes both exact and instant.
		// The timers must be installed before the hooks mount, or TanStack arms its interval on the real clock and the
		// negative check passes for the wrong reason. `vi.waitFor` (not RTL's) is used throughout because it is the one
		// that advances fake timers; RTL's only detects Jest's.
		vi.useFakeTimers();
		try {
			const { result } = renderHook(
				() => ({ comparisons: useBenchmarkComparisons(projectId), runs: useBenchmarkRuns(projectId) }),
				{ wrapper },
			);
			await vi.waitFor(
				() => {
					expect(result.current.runs.isSuccess).toBe(true);
					expect(result.current.comparisons.isSuccess).toBe(true);
				},
				{ interval: 1 },
			);
			// The first read has nothing to compare against, and the list it would refresh came from the same node state.
			expect(comparisonReads).toBe(1);
			expect(listReads).toBe(1);

			// One poll on: a verdict landed, but the cohort and the comparison set are the same reading, so nothing is
			// invalidated and the ranked table is not refetched.
			await vi.advanceTimersByTimeAsync(activeComparisonPollMs);
			await vi.waitFor(() => expect(comparisonReads).toBe(2), { interval: 1 });
			expect(listReads).toBe(1);

			// The next poll carries the fit, and the ranked table is refreshed with it exactly once.
			await vi.advanceTimersByTimeAsync(activeComparisonPollMs);
			await vi.waitFor(() => expect(listReads).toBe(2), { interval: 1 });
			expect(result.current.comparisons.data?.fit?.fitKey).toBe("fit-1");

			// Every comparison is terminal now, so the verdicts stop being re-read and the table stops being refreshed with
			// them: one fit, one refresh. A predicate that fired on any change instead would keep the table on the wire.
			const readsAtFit = comparisonReads;
			await vi.advanceTimersByTimeAsync(activeComparisonPollMs + 500);
			expect(comparisonReads).toBe(readsAtFit);
			expect(listReads).toBe(2);
		} finally {
			vi.useRealTimers();
		}
	});

	it("re-judges a whole project and reports how many runs were enqueued", async () => {
		let observedBody: unknown;
		server.use(
			http.post(localApiPath(`benchmarks/projects/${projectId}/rejudge`), async ({ request }) => {
				observedBody = await request.json();
				return HttpResponse.json({ project: projectDetail({ version: 3 }), enqueuedRunIds: [runId, "dddddddd-0000-4000-8000-000000000004"] });
			}),
		);
		const { wrapper } = createProvidersWrapper();

		const { result } = renderHook(() => useRejudgeBenchmarkProject(), { wrapper });

		result.current.mutate({ projectId, expectedVersion: 2 });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(observedBody).toEqual({ expectedVersion: 2 });
		expect(result.current.data?.enqueuedRunCount).toBe(2);
	});
});
