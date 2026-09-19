// @vitest-environment jsdom

// The two lifecycle controls on an evaluation row. What is pinned here is that the UI partitions the SERVER's state
// machine rather than a guess at it: cancel is offered only where `CancelEvaluationEndpoint` acts (Queued/Running —
// anything else answers 404), delete only where `DeleteEvaluationEndpoint` acts (terminal AND unbound — a bound or
// still-active evaluation is a 409). The evaluation list query is real and answered by MSW, so "the row is gone"
// means the refetch after the delete really returned without it, not that a mock forgot it.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { ComparisonCreateDialog } from "@/features/training/components/ComparisonCreateDialog";
import { localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const runId = "11111111-1111-1111-1111-111111111111";
const evaluationId = "22222222-2222-2222-2222-222222222222";
const comparisonId = "33333333-3333-3333-3333-333333333333";
const datasetId = "44444444-4444-4444-4444-444444444444";

// The row's own progress line. It is the one text unique to an existing evaluation row: the button labels are shared
// with the confirmation dialog ("Delete evaluation" is also its accept label) and with the other, unevaluated side.
const terminalRowProgress = "4 of 4 scored, 1 passed";

const toastMock = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), info: vi.fn(), warn: vi.fn(), warning: vi.fn() }));
const hubMock = vi.hoisted(() => ({ resync: (): void => undefined }));

vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));
// SignalR has no place in jsdom, and capturing the hub's resync callback is what lets the last test replay the exact
// path a live progress push takes: it invalidates the list, it never writes a row into the cache by hand.
vi.mock("@/features/training/hooks/useTrainingRunHub", () => ({
	useTrainingRunHub: (_runId: string | null, onResync: () => void) => {
		hubMock.resync = onResync;
		return { step: 0, totalSteps: 0, loss: null, phase: null };
	},
}));
vi.mock("@/features/benchmarks/queries/useBenchmarks", () => ({
	useBenchmarkProjects: () => ({ data: [] }),
	useBenchmarkRuns: () => ({ data: { items: [] } }),
}));
vi.mock("@/features/training/queries/useTrainingRuns", () => ({
	useTrainingRuns: () => ({ data: [{ id: runId, status: "Succeeded" }] }),
}));
// Only the lineage suggestion is stubbed; the evaluation list, the cancel and the delete all go over the wire. The
// tuned side is left without a model name so exactly one evaluation row exists to make assertions about.
vi.mock("@/features/training/queries/useTrainingComparisons", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/features/training/queries/useTrainingComparisons")>()),
	useComparisonSuggestion: () => ({
		data: {
			trainingRunId: runId,
			baseModelName: "base-model",
			tunedModelName: null,
			baseEvaluationRunId: evaluationId,
			tunedEvaluationRunId: null,
			unavailableReason: null,
		},
	}),
}));

interface EvaluationRowOverrides {
	status?: string;
	comparisonId?: string | null;
}

function evaluationRow({ status = "Succeeded", comparisonId: bound = null }: EvaluationRowOverrides = {}) {
	return {
		id: evaluationId,
		trainingRunId: runId,
		comparisonId: bound,
		modelName: "base-model",
		targetKind: "InstalledModel",
		datasetId,
		datasetContentFingerprint: "v1:dataset",
		status,
		totalCount: 4,
		scoredCount: status === "Succeeded" ? 4 : 1,
		passedCount: 1,
		perKind: [],
		version: 7,
		createdAtUtc: 1_700_000_000_000,
		updatedAtUtc: 1_700_000_100_000,
	};
}

/** The list route, plus a handle on what it answers so a test can make the node forget the row it just deleted. */
function serveEvaluations(...initial: ReturnType<typeof evaluationRow>[]): { rows: ReturnType<typeof evaluationRow>[] } {
	const state = { rows: initial };
	server.use(http.get(localApiPath("training/evaluations"), () => HttpResponse.json({ items: state.rows })));
	return state;
}

/** Counts the commands that actually reached the wire — a confirmation that leaks one is the bug this guards. */
function recordCalls(method: "post" | "delete", path: string): { count: number } {
	const seen = { count: 0 };
	server.use(
		http[method](localApiPath(path), () => {
			seen.count += 1;
			return new HttpResponse(null, { status: 204 });
		}),
	);
	return seen;
}

function renderDialog() {
	return renderWithProviders(
		<ConfirmProvider>
			<ComparisonCreateDialog initialRunId={runId} onClose={vi.fn()} opened={true} />
		</ConfirmProvider>,
	);
}

describe("ComparisonCreateDialog evaluation lifecycle", () => {
	beforeEach(() => {
		hubMock.resync = () => undefined;
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("offers cancel while the evaluation is queued, and sends it to the node", async () => {
		serveEvaluations(evaluationRow({ status: "Queued" }));
		const cancels = recordCalls("post", `training/evaluations/${evaluationId}/cancel`);
		renderDialog();

		const cancel = await screen.findByRole("button", { name: "Cancel evaluation" });
		expect(screen.queryByRole("button", { name: "Delete evaluation" })).toBeNull();

		fireEvent.click(cancel);

		await waitFor(() => expect(cancels.count).toBe(1));
		expect(toastMock.success).toHaveBeenCalledWith("Cancelling the evaluation.");
	});

	it("reports a running evaluation as cancelling until the executor settles it", async () => {
		// Cancelling a RUNNING evaluation only signals the executor, which owns the terminal write, so the list keeps
		// answering `Running`. The row has to say the command was accepted, or it reads as if nothing happened.
		serveEvaluations(evaluationRow({ status: "Running" }));
		recordCalls("post", `training/evaluations/${evaluationId}/cancel`);
		renderDialog();

		fireEvent.click(await screen.findByRole("button", { name: "Cancel evaluation" }));

		expect(await screen.findByText("Cancelling…")).toBeDefined();
		await waitFor(() =>
			expect((screen.getByRole("button", { name: "Cancel evaluation" }) as HTMLButtonElement).disabled).toBe(true),
		);
	});

	it("treats a cancel the node answers 404 as already done, not as a failure", async () => {
		// `CancelEvaluationEndpoint` answers 404 for an evaluation that is already terminal, and a row can be stale by
		// the width of the asynchronous Running-cancel window. Blaming the operator for that would be a lie: the
		// evaluation IS cancelled. The refetch is the whole remedy, and it is what makes the row tell the truth.
		const listed = serveEvaluations(evaluationRow({ status: "Running" }));
		server.use(problemDetailsRoute("post", `training/evaluations/${evaluationId}/cancel`, 404, { detail: "Not Found" }));
		renderDialog();

		fireEvent.click(await screen.findByRole("button", { name: "Cancel evaluation" }));
		listed.rows = [evaluationRow({ status: "Cancelled" })];

		// The row settles to the status the node really holds, and no cancel control is offered on it any more.
		expect(await screen.findByText("Cancelled")).toBeDefined();
		await waitFor(() => expect(screen.queryByRole("button", { name: "Cancel evaluation" })).toBeNull());
		expect(toastMock.error).not.toHaveBeenCalled();
	});

	it("treats a delete the node answers 404 as already done, not as a failure", async () => {
		const listed = serveEvaluations(evaluationRow());
		server.use(problemDetailsRoute("delete", `training/evaluations/${evaluationId}`, 404, { detail: "Not Found" }));
		renderDialog();

		fireEvent.click(await screen.findByRole("button", { name: "Delete evaluation" }));
		listed.rows = [];
		fireEvent.click(await screen.findByTestId("confirm-accept"));

		await waitFor(() => expect(screen.queryByText(terminalRowProgress)).toBeNull());
		expect(toastMock.error).not.toHaveBeenCalled();
	});

	it("offers delete only on a terminal evaluation no comparison report binds", async () => {
		const bound = serveEvaluations(evaluationRow({ comparisonId }));
		renderDialog();

		const boundDelete = (await screen.findByRole("button", { name: "Delete evaluation" })) as HTMLButtonElement;
		expect(boundDelete.disabled).toBe(true);
		expect(screen.queryByRole("button", { name: "Cancel evaluation" })).toBeNull();
		cleanup();

		bound.rows = [evaluationRow()];
		renderDialog();
		const unbound = (await screen.findByRole("button", { name: "Delete evaluation" })) as HTMLButtonElement;
		expect(unbound.disabled).toBe(false);
	});

	it("deletes the evaluation once the operator confirms, and the refetched list no longer carries it", async () => {
		const listed = serveEvaluations(evaluationRow());
		const deletes = recordCalls("delete", `training/evaluations/${evaluationId}`);
		renderDialog();

		fireEvent.click(await screen.findByRole("button", { name: "Delete evaluation" }));
		expect(await screen.findByText("Delete this evaluation?")).toBeDefined();
		// The node forgets the row exactly as the real DELETE does, so the invalidated query refetches it away.
		listed.rows = [];
		fireEvent.click(screen.getByTestId("confirm-accept"));

		await waitFor(() => expect(deletes.count).toBe(1));
		await waitFor(() => expect(screen.queryByText(terminalRowProgress)).toBeNull());
		expect(toastMock.success).toHaveBeenCalledWith("The evaluation was deleted.");
	});

	it("sends nothing and keeps the row when the operator backs out of the delete", async () => {
		serveEvaluations(evaluationRow());
		const deletes = recordCalls("delete", `training/evaluations/${evaluationId}`);
		renderDialog();

		fireEvent.click(await screen.findByRole("button", { name: "Delete evaluation" }));
		fireEvent.click(await screen.findByTestId("confirm-cancel"));

		await waitFor(() => expect(screen.queryByText("Delete this evaluation?")).toBeNull());
		expect(deletes.count).toBe(0);
		expect(screen.getByText(terminalRowProgress)).toBeDefined();
	});

	it("surfaces the node's refusal and keeps the row when the delete is rejected", async () => {
		serveEvaluations(evaluationRow());
		server.use(
			problemDetailsRoute("delete", `training/evaluations/${evaluationId}`, 409, {
				detail: "The evaluation run is part of a comparison report.",
			}),
		);
		renderDialog();

		fireEvent.click(await screen.findByRole("button", { name: "Delete evaluation" }));
		fireEvent.click(await screen.findByTestId("confirm-accept"));

		await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("The evaluation run is part of a comparison report."));
		// The comparison dialog itself is untouched by a refused delete: the row, and the way back out, are still there.
		expect(screen.getByText(terminalRowProgress)).toBeDefined();
		expect(screen.getByRole("button", { name: "Create report" })).toBeDefined();
	});

	it("does not resurrect a deleted row when a hub push refreshes the list", async () => {
		const listed = serveEvaluations(evaluationRow());
		recordCalls("delete", `training/evaluations/${evaluationId}`);
		renderDialog();

		fireEvent.click(await screen.findByRole("button", { name: "Delete evaluation" }));
		listed.rows = [];
		fireEvent.click(await screen.findByTestId("confirm-accept"));
		await waitFor(() => expect(screen.queryByText(terminalRowProgress)).toBeNull());

		// The hub handler invalidates rather than writing a row into the cache, so the push can only re-read the node.
		hubMock.resync();

		await waitFor(() => expect(screen.getAllByRole("button", { name: "Evaluate" })).toHaveLength(2));
		expect(screen.queryByText(terminalRowProgress)).toBeNull();
	});
});
