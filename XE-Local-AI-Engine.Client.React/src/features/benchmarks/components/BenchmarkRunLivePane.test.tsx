// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The live pane's only own logic is wiring: pick the run out of the query, hand the hub's parts to the presentational
// pane, and route each mutation's failure to a toast. The SignalR hub itself has its own suite (useBenchmarkRunHub),
// so it is stubbed here — everything else, including the run read and the score/cancel/delete writes, goes over MSW
// through the real generated client.
const { hubMock, toastErrorMock } = vi.hoisted(() => ({
	hubMock: vi.fn(),
	toastErrorMock: vi.fn(),
}));

vi.mock("@/features/benchmarks/hooks/useBenchmarkRunHub", () => ({ useBenchmarkRunHub: hubMock }));
vi.mock("@/core/ui/notifications/Toast", () => ({ toast: { error: toastErrorMock, success: vi.fn(), info: vi.fn() } }));

import { BenchmarkRunLivePane } from "@/features/benchmarks/components/BenchmarkRunLivePane";
import { noBenchmarkRunLiveOverlay } from "@/features/benchmarks/models/BenchmarkModels";
import { domainErrorRoute, jsonRoute, localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const runId = "bbbbbbbb-0000-4000-8000-000000000002";
const projectId = "aaaaaaaa-0000-4000-8000-000000000001";

// The pane reads the run's project for the judging MODE alone. Inside the page that key is already warm, so the
// component's own comment is right that it costs no request; mounted on its own here it is a real read, ambient to
// every test in the file and belonging to none of them.
setupMswServer(
	jsonRoute("get", `benchmarks/projects/${projectId}`, {
		id: projectId,
		name: "Summarisation",
		coreTask: "Summarise the attached text.",
		contextTokens: 4096,
		agentDefinitionId: "cccccccc-0000-4000-8000-000000000003",
		judgeEnabled: false,
		judge: { enabled: false },
		runCount: 1,
		isFrozen: true,
		version: 2,
		createdAtUtc: 1,
		updatedAtUtc: 2,
	}),
);

function runRow(overrides: Record<string, unknown> = {}) {
	return {
		id: runId,
		projectId,
		primaryModelName: "model.gguf",
		primaryModelOrigin: "imported",
		modelContentFingerprint: "v1:test",
		agentName: "Summariser",
		agentVersion: 1,
		requestedContextTokens: 4096,
		primaryStatus: "Succeeded",
		judge: { state: "none", policyCurrent: false, executionCurrent: false },
		qualityScoreSource: "none",
		modelGroupKey: "v1:test",
		effectiveContextTokens: 4096,
		durationMs: 1250,
		totalTokens: 30,
		tokensPerSecond: 24,
		userScore: null,
		lastStreamSequence: 2,
		version: 3,
		createdAtUtc: 1,
		updatedAtUtc: 2,
		outputParts: [{ kind: "output", content: "Persisted answer" }],
		...overrides,
	};
}

describe("BenchmarkRunLivePane", () => {
	beforeEach(() => {
		hubMock.mockReturnValue({ parts: [], overlay: noBenchmarkRunLiveOverlay, isConnected: true, isReconnecting: false });
		toastErrorMock.mockClear();
	});

	afterEach(cleanup);

	it("renders the run and the parts the hub hands it", async () => {
		hubMock.mockReturnValue({
			parts: [{ kind: "output", content: "Persisted answer" }],
			overlay: noBenchmarkRunLiveOverlay,
			isConnected: true,
			isReconnecting: false,
		});
		server.use(http.get(localApiPath(`benchmarks/runs/${runId}`), () => HttpResponse.json(runRow())));

		renderWithProviders(<BenchmarkRunLivePane runId={runId} />);

		expect(await screen.findByText("Persisted answer")).toBeTruthy();
		// Run-level metadata comes from the read, not from the hub.
		expect(screen.getByText("Imported")).toBeTruthy();
	});

	// A read failure is a query load-error, so it belongs in an inline Alert rather than a toast.
	it("shows a load failure inline with the server's message", async () => {
		server.use(
			problemDetailsRoute("get", `benchmarks/runs/${runId}`, 500, { title: "Server Error", detail: "The run store is offline." }),
		);

		renderWithProviders(<BenchmarkRunLivePane runId={runId} />);

		expect(await screen.findByText("The run store is offline.")).toBeTruthy();
		expect(toastErrorMock).not.toHaveBeenCalled();
	});

	// The hub owns the live parts; the pane must render those rather than the persisted snapshot while streaming.
	it("renders the hub's live parts, not the persisted snapshot", async () => {
		hubMock.mockReturnValue({
			parts: [{ kind: "output", content: "Streaming answer" }],
			overlay: noBenchmarkRunLiveOverlay,
			isConnected: true,
			isReconnecting: false,
		});
		server.use(http.get(localApiPath(`benchmarks/runs/${runId}`), () => HttpResponse.json(runRow())));

		renderWithProviders(<BenchmarkRunLivePane runId={runId} />);

		expect(await screen.findByText("Streaming answer")).toBeTruthy();
		expect(screen.queryByText("Persisted answer")).toBeNull();
	});

	it("posts the operator score with the run's expected version", async () => {
		let observedBody: unknown;
		server.use(
			http.get(localApiPath(`benchmarks/runs/${runId}`), () => HttpResponse.json(runRow())),
			http.put(localApiPath(`benchmarks/runs/${runId}/score`), async ({ request }) => {
				observedBody = await request.json();
				return HttpResponse.json(runRow({ userScore: 100, version: 4 }));
			}),
		);

		renderWithProviders(<BenchmarkRunLivePane runId={runId} />);

		fireEvent.click(await screen.findByTestId("benchmark-score-preset-100"));

		await waitFor(() => expect(observedBody).toEqual({ score: 100, expectedVersion: 3 }));
		expect(toastErrorMock).not.toHaveBeenCalled();
	});

	// A losing score race is the expected failure of the optimistic-concurrency contract, and it IS a mutation result —
	// so it goes to a toast carrying the server's sentence, not to an inline alert.
	it("toasts the server's message when the score loses a version race", async () => {
		server.use(
			http.get(localApiPath(`benchmarks/runs/${runId}`), () => HttpResponse.json(runRow())),
			domainErrorRoute("put", `benchmarks/runs/${runId}/score`, 409, {
				reason: "VersionConflict",
				message: "The run changed before the score was saved.",
			}),
		);

		renderWithProviders(<BenchmarkRunLivePane runId={runId} />);

		fireEvent.click(await screen.findByTestId("benchmark-score-preset-25"));

		await waitFor(() => expect(toastErrorMock).toHaveBeenCalledWith("The run changed before the score was saved."));
	});
	// Clearing the override is its own verb on the wire; posting a 0 would be a different, real score.
	it("clears the operator override through DELETE, carrying the run version", async () => {
		let observedBody: unknown;
		server.use(
			http.get(localApiPath(`benchmarks/runs/${runId}`), () => HttpResponse.json(runRow({ userScore: 60 }))),
			http.delete(localApiPath(`benchmarks/runs/${runId}/score`), async ({ request }) => {
				observedBody = await request.json();
				return HttpResponse.json(runRow({ userScore: null, version: 4 }));
			}),
		);

		renderWithProviders(<BenchmarkRunLivePane runId={runId} />);

		fireEvent.click(await screen.findByTestId("benchmark-score-clear"));

		await waitFor(() => expect(observedBody).toEqual({ expectedVersion: 3 }));
		expect(toastErrorMock).not.toHaveBeenCalled();
	});

	// The hub's JudgeState events correct what is RENDERED; the durable snapshot stays the write target.
	it("renders the hub's judge state over the persisted one", async () => {
		hubMock.mockReturnValue({
			parts: [],
			overlay: { ...noBenchmarkRunLiveOverlay, judgeState: "running" },
			isConnected: true,
			isReconnecting: false,
		});
		server.use(http.get(localApiPath(`benchmarks/runs/${runId}`), () => HttpResponse.json(runRow())));

		renderWithProviders(<BenchmarkRunLivePane runId={runId} />);

		expect((await screen.findByTestId("benchmark-judge-state")).textContent).toBe("Judging");
	});

	// An explicit re-judge is forced: the operator asked for a fresh verdict, not for an idempotent no-op.
	it("forces the re-judge it posts for one run", async () => {
		let observedBody: unknown;
		server.use(
			http.get(localApiPath(`benchmarks/runs/${runId}`), () => HttpResponse.json(runRow())),
			http.post(localApiPath(`benchmarks/runs/${runId}/rejudge`), async ({ request }) => {
				observedBody = await request.json();
				return HttpResponse.json(runRow({ judge: { state: "queued", policyCurrent: true, executionCurrent: false } }));
			}),
		);

		renderWithProviders(<BenchmarkRunLivePane runId={runId} />);

		fireEvent.click(await screen.findByRole("button", { name: "Re-judge run" }));

		await waitFor(() => expect(observedBody).toEqual({ expectedVersion: 3, force: true }));
	});

	describe("in a review project", () => {
		// No judge policy, so the form reviews one overall score; the run names no item, so it counts as scoring.
		const reviewProject = () =>
			server.use(
				jsonRoute("get", `benchmarks/projects/${projectId}`, {
					id: projectId,
					name: "Reviewed",
					coreTask: "Summarise the attached text.",
					contextTokens: 4096,
					agentDefinitionId: "cccccccc-0000-4000-8000-000000000003",
					judgeEnabled: false,
					requiresReview: true,
					judge: { enabled: false },
					runCount: 1,
					isFrozen: true,
					version: 2,
					createdAtUtc: 1,
					updatedAtUtc: 2,
				}),
				jsonRoute("get", `benchmarks/projects/${projectId}/items`, { items: [], projectVersion: 2 }),
				http.get(localApiPath(`benchmarks/runs/${runId}`), () => HttpResponse.json(runRow())),
			);

		it("puts the review with the run's expected version instead of offering a plain score", async () => {
			let observedBody: unknown;
			reviewProject();
			server.use(
				http.put(localApiPath(`benchmarks/runs/${runId}/review`), async ({ request }) => {
					observedBody = await request.json();
					return HttpResponse.json(runRow({ version: 4 }));
				}),
			);

			renderWithProviders(<BenchmarkRunLivePane runId={runId} />);

			fireEvent.change(await screen.findByTestId("benchmark-review-overall"), { target: { value: "64" } });
			expect(screen.queryByTestId("benchmark-score-save")).toBeNull();
			fireEvent.click(screen.getByTestId("benchmark-review-approve"));

			await waitFor(() => expect(observedBody).toEqual({ state: "approved", score: 64, notes: null, expectedVersion: 3 }));
			expect(toastErrorMock).not.toHaveBeenCalled();
		});

		it("names a NotReviewable refusal rather than relaying a bare 409", async () => {
			reviewProject();
			server.use(
				http.put(localApiPath(`benchmarks/runs/${runId}/review`), () =>
					HttpResponse.json(
						{
							type: "about:blank",
							title: "Conflict",
							status: 409,
							detail: "This run cannot be reviewed.",
							code: "NotReviewable",
						},
						{ status: 409, headers: { "content-type": "application/problem+json" } },
					),
				),
			);

			let runReads = 0;
			server.use(
				http.get(localApiPath(`benchmarks/runs/${runId}`), () => {
					runReads += 1;
					return HttpResponse.json(runRow());
				}),
			);

			renderWithProviders(<BenchmarkRunLivePane runId={runId} />);

			fireEvent.click(await screen.findByTestId("benchmark-review-reject"));
			// The judging moved under the pane, so the run is re-read rather than left showing an open form.
			await waitFor(() => expect(runReads).toBe(2));

			await waitFor(() =>
				expect(toastErrorMock).toHaveBeenCalledWith(
					"This run cannot be reviewed yet. Re-judge it under the current judge policy first.",
				),
			);
		});

		// A NIAH case keeps its judge path; the node refuses both a plain score and a review on it.
		it("offers no score control on a non-scoring leaf", async () => {
			reviewProject();
			server.use(
				jsonRoute("get", `benchmarks/projects/${projectId}/items`, {
					items: [
						{
							id: "dddddddd-0000-4000-8000-000000000004",
							projectId,
							index: 0,
							kind: "niahCase",
							revision: 1,
							inputHash: "v1:h",
							isLeaf: true,
							countsTowardScore: false,
							prompt: "Find the needle.",
						},
					],
					projectVersion: 2,
				}),
				http.get(localApiPath(`benchmarks/runs/${runId}`), () =>
					HttpResponse.json(runRow({ taskItemId: "dddddddd-0000-4000-8000-000000000004" })),
				),
			);

			renderWithProviders(<BenchmarkRunLivePane runId={runId} />);

			expect((await screen.findByTestId("benchmark-score-judge-only")).textContent).toContain("not reviewable");
			expect(screen.queryByTestId("benchmark-score-save")).toBeNull();
			expect(screen.queryByTestId("benchmark-review-form")).toBeNull();
		});

		it("refetches the run and says so when the review loses a version race", async () => {
			reviewProject();
			let runReads = 0;
			server.use(
				http.get(localApiPath(`benchmarks/runs/${runId}`), () => {
					runReads += 1;
					return HttpResponse.json(runRow());
				}),
				http.put(localApiPath(`benchmarks/runs/${runId}/review`), () =>
					HttpResponse.json(
						{ type: "about:blank", title: "Conflict", status: 409, detail: "Stale version.", code: "VersionConflict" },
						{ status: 409, headers: { "content-type": "application/problem+json" } },
					),
				),
			);

			renderWithProviders(<BenchmarkRunLivePane runId={runId} />);

			fireEvent.click(await screen.findByTestId("benchmark-review-reject"));

			await waitFor(() =>
				expect(toastErrorMock).toHaveBeenCalledWith("The run changed before the review was saved. It has been refreshed."),
			);
			expect(runReads).toBe(2);
		});

		// A rejection clears the run's score: the pane re-reads the run and shows the decision with no score beside it.
		it("shows a rejection with no score once the review is saved", async () => {
			reviewProject();
			let decided = false;
			server.use(
				http.get(localApiPath(`benchmarks/runs/${runId}`), () =>
					HttpResponse.json(
						decided
							? runRow({
									version: 4,
									review: { state: "rejected", score: null, criteria: [], policyCurrent: true, updatedAtUtc: 5 },
								})
							: runRow({ userScore: 70, qualityScore: 70, qualityScoreSource: "user" }),
					),
				),
				http.put(localApiPath(`benchmarks/runs/${runId}/review`), () => {
					decided = true;
					return HttpResponse.json(runRow({ version: 4 }));
				}),
			);

			renderWithProviders(<BenchmarkRunLivePane runId={runId} />);
			fireEvent.click(await screen.findByTestId("benchmark-review-reject"));

			const stored = await screen.findByTestId("benchmark-review-stored");
			expect(stored.textContent).toContain("rejected");
			expect(stored.textContent).not.toContain("/ 100");
			expect(document.body.textContent).toContain("Quality: —");
		});
	});
});
