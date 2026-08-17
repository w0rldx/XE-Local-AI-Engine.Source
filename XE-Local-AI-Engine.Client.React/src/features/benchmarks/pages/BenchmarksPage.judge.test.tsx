// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The judge-change flow is the one page behaviour that cannot be tested through a component: the node answers the
// first save with 409 `RejudgeRequired` on purpose, and the page has to turn that into a confirmation and resend the
// SAME policy with the flag set. The SignalR hub is stubbed (a selected run mounts the live pane) — it has its own
// suite; everything else goes over MSW through the real generated client.
const { hubMock, toastErrorMock } = vi.hoisted(() => ({ hubMock: vi.fn(), toastErrorMock: vi.fn() }));

vi.mock("@/features/benchmarks/hooks/useBenchmarkRunHub", () => ({ useBenchmarkRunHub: hubMock }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: { error: toastErrorMock, success: vi.fn(), info: vi.fn() } }));

import { noBenchmarkRunLiveOverlay } from "@/features/benchmarks/models/BenchmarkModels";
import { BenchmarksPage } from "@/features/benchmarks/pages/BenchmarksPage";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";

const projectId = "aaaaaaaa-0000-4000-8000-000000000001";
const runId = "bbbbbbbb-0000-4000-8000-000000000002";

const rubric = { version: 1, criteria: [{ id: "accuracy", title: "Accuracy", description: "Are the facts right?", weight: 50 }] };

const projectRow = {
	id: projectId,
	name: "Summarisation",
	contextTokens: 4096,
	agentDefinitionId: "cccccccc-0000-4000-8000-000000000003",
	judgeEnabled: true,
	runCount: 1,
	isFrozen: true,
	version: 2,
	createdAtUtc: 1,
	updatedAtUtc: 2,
};

const projectDetail = {
	...projectRow,
	coreTask: "Summarise the attached text.",
	judge: {
		enabled: true,
		policyRevision: 1,
		modelName: "judge.gguf",
		requestedContextTokens: 8192,
		rubric,
		referenceAnswer: null,
		cohortGeneration: 1,
	},
};

function runRow(overrides: Record<string, unknown> = {}) {
	return {
		id: runId,
		projectId,
		primaryModelName: "model.gguf",
		modelContentFingerprint: "v1:test",
		modelGroupKey: "v1:test",
		agentName: "Summariser",
		agentVersion: 1,
		requestedContextTokens: 4096,
		primaryStatus: "Succeeded",
		judge: { state: "succeeded", score: 60, policyRevision: 1, policyCurrent: true, executionCurrent: true },
		qualityScore: 60,
		qualityScoreSource: "judge",
		rank: 1,
		version: 3,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		outputParts: [],
		...overrides,
	};
}

function baseRoutes(run: Record<string, unknown> = runRow()) {
	server.use(
		jsonRoute("get", "benchmarks/projects", { items: [projectRow] }),
		jsonRoute("get", "benchmarks/eligible-models", {
			items: [{ modelName: "judge.gguf", modelContentFingerprint: "v1:judge" }],
		}),
		jsonRoute("get", "agents", { items: [] }),
		jsonRoute("get", "benchmarks/rubric-presets", { default: rubric, programming: rubric, reasoning: rubric }),
		jsonRoute("get", `benchmarks/projects/${projectId}`, projectDetail),
		jsonRoute("get", `benchmarks/projects/${projectId}/runs`, {
			items: [run],
			rankCohort: { policyRevision: 1, cohortGeneration: 1, rankedCount: 1, totalScored: 1 },
		}),
		jsonRoute("get", `benchmarks/runs/${runId}`, run),
	);
}

describe("BenchmarksPage judge changes", () => {
	beforeEach(() => {
		hubMock.mockReturnValue({ parts: [], overlay: noBenchmarkRunLiveOverlay, isConnected: false, isReconnecting: false });
		toastErrorMock.mockClear();
	});

	afterEach(cleanup);

	it("confirms the re-judge a judge change implies and resends the same policy", async () => {
		const bodies: Record<string, unknown>[] = [];
		baseRoutes();
		server.use(
			http.put(localApiPath(`benchmarks/projects/${projectId}/judge`), async ({ request }) => {
				bodies.push((await request.json()) as Record<string, unknown>);
				if (bodies.length === 1) {
					return HttpResponse.json(
						{ type: "about:blank", title: "Conflict", status: 409, detail: "Confirm the re-judge.", code: "RejudgeRequired" },
						{ status: 409, headers: { "content-type": "application/problem+json" } },
					);
				}
				return HttpResponse.json({ project: { ...projectDetail, version: 3 }, enqueuedRunIds: [runId] });
			}),
		);

		renderWithProviders(<BenchmarksPage />);

		fireEvent.click(await screen.findByRole("button", { name: "Edit judge" }));
		fireEvent.click(await screen.findByRole("button", { name: "Save judge" }));

		// The first save is refused, and the refusal has to read as a question rather than as an error toast.
		expect(await screen.findByText(/All 1 succeeded runs will be re-judged/)).toBeTruthy();
		expect(toastErrorMock).not.toHaveBeenCalled();

		fireEvent.click(screen.getByTestId("benchmark-rejudge-confirm-accept"));

		await waitFor(() => expect(bodies).toHaveLength(2));
		expect(bodies[0]?.["confirmRejudge"]).toBe(false);
		expect(bodies[1]?.["confirmRejudge"]).toBe(true);
		// The same policy is resent — a confirmation must never silently change what it confirms.
		expect(bodies[1]?.["policy"]).toEqual(bodies[0]?.["policy"]);
	});

	// The node refuses a judge change while a judging is still running; that is a wait, not a bad request.
	it("explains an active judging instead of asking for a confirmation", async () => {
		baseRoutes();
		server.use(
			http.put(localApiPath(`benchmarks/projects/${projectId}/judge`), () =>
				HttpResponse.json(
					{ type: "about:blank", title: "Conflict", status: 409, detail: "Still running.", code: "JudgeAttemptsActive" },
					{ status: 409, headers: { "content-type": "application/problem+json" } },
				),
			),
		);

		renderWithProviders(<BenchmarksPage />);

		fireEvent.click(await screen.findByRole("button", { name: "Edit judge" }));
		fireEvent.click(await screen.findByRole("button", { name: "Save judge" }));

		await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith(expect.stringContaining("still running")));
		expect(screen.queryByTestId("benchmark-rejudge-confirm-accept")).toBeNull();
	});

	it("re-judges the whole project once the operator confirms", async () => {
		let observedBody: unknown;
		baseRoutes();
		server.use(
			http.post(localApiPath(`benchmarks/projects/${projectId}/rejudge`), async ({ request }) => {
				observedBody = await request.json();
				return HttpResponse.json({ project: { ...projectDetail, version: 3 }, enqueuedRunIds: [runId] });
			}),
		);

		renderWithProviders(<BenchmarksPage />);

		fireEvent.click(await screen.findByTestId("benchmark-rejudge-all"));
		fireEvent.click(await screen.findByTestId("benchmark-rejudge-confirm-accept"));

		await waitFor(() => expect(observedBody).toEqual({ expectedVersion: 2 }));
	});

	// A project re-judge is refused by the node while any attempt is active, so the button says so up front.
	it("disables the project re-judge while a judging is active", async () => {
		baseRoutes(runRow({ judge: { state: "running", policyRevision: 1, policyCurrent: true, executionCurrent: false } }));

		renderWithProviders(<BenchmarksPage />);

		expect(((await screen.findByTestId("benchmark-rejudge-all")) as HTMLButtonElement).disabled).toBe(true);
	});
});
