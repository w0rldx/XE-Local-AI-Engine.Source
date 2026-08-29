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
import { domainErrorRoute, localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const runId = "bbbbbbbb-0000-4000-8000-000000000002";
const projectId = "aaaaaaaa-0000-4000-8000-000000000001";

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
});
