// @vitest-environment jsdom

// The toolbar owns the one destructive control on the run view, so what is pinned is when it is offered and what it
// takes to fire it: a confirmation before the request, no second command while the run is already draining or done,
// and a way back to the editor that changes the selection rather than the run.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { GraphWorkflowRunToolbar } from "@/features/graphWorkflows/components/GraphWorkflowRunToolbar";
import type { GraphWorkflowRunSummaryResponse } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { graphWorkflowRunSummary, graphWorkflowTestIds } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const runId = graphWorkflowTestIds.run;
const cancelPath = localApiPath(`graph-workflows/runs/${runId}/cancel`);

/** Counts the cancel commands that actually reached the wire — a confirmation that leaks one is the bug here. */
function recordCancels(): { count: number } {
	const seen = { count: 0 };
	server.use(
		http.post(cancelPath, () => {
			seen.count += 1;
			return HttpResponse.json({}, { status: 202 });
		}),
	);
	return seen;
}

function renderToolbar(run: GraphWorkflowRunSummaryResponse, onBackToEditor = vi.fn()) {
	const result = renderWithProviders(
		<ConfirmProvider>
			<GraphWorkflowRunToolbar run={run} onBackToEditor={onBackToEditor} />
		</ConfirmProvider>,
	);
	return { ...result, onBackToEditor };
}

describe("GraphWorkflowRunToolbar", () => {
	afterEach(() => {
		cleanup();
	});

	it("shows the run's status and the definition version it pinned", () => {
		renderToolbar(graphWorkflowRunSummary({ status: "Running", definitionVersion: 4 }));

		expect(screen.getByTestId("graph-workflow-run-status-badge").textContent).toBe("Running");
		expect(screen.getByTestId("graph-workflow-run-toolbar-version").textContent).toBe("definition version 4");
		expect(screen.getByTestId("graph-workflow-run-toolbar-started")).toBeDefined();
	});

	it("asks before cancelling, and sends nothing when the operator backs out", async () => {
		const cancels = recordCancels();
		renderToolbar(graphWorkflowRunSummary({ status: "Running" }));

		fireEvent.click(screen.getByTestId("graph-workflow-run-toolbar-cancel"));

		expect(await screen.findByText("Cancel this run?")).toBeDefined();
		fireEvent.click(screen.getByTestId("confirm-cancel"));

		await waitFor(() => expect(screen.queryByText("Cancel this run?")).toBeNull());
		expect(cancels.count).toBe(0);
	});

	it("sends the cancel once the operator confirms it", async () => {
		const cancels = recordCancels();
		renderToolbar(graphWorkflowRunSummary({ status: "WaitingForApproval" }));

		fireEvent.click(screen.getByTestId("graph-workflow-run-toolbar-cancel"));
		fireEvent.click(await screen.findByTestId("confirm-accept"));

		await waitFor(() => expect(cancels.count).toBe(1));
	});

	it("offers no cancel on a run that has finished or is already draining", () => {
		// `Cancelling` is the drain: the command was accepted and a second one would change nothing.
		const draining = renderToolbar(graphWorkflowRunSummary({ status: "Cancelling" }));
		expect((screen.getByTestId("graph-workflow-run-toolbar-cancel") as HTMLButtonElement).disabled).toBe(true);
		draining.unmount();

		const completed = renderToolbar(graphWorkflowRunSummary({ status: "Completed", completedAtUtc: 1_700_000_300_000 }));
		expect((screen.getByTestId("graph-workflow-run-toolbar-cancel") as HTMLButtonElement).disabled).toBe(true);
		expect(screen.getByTestId("graph-workflow-run-toolbar-completed")).toBeDefined();
		completed.unmount();

		renderToolbar(graphWorkflowRunSummary({ status: "Running" }));
		expect((screen.getByTestId("graph-workflow-run-toolbar-cancel") as HTMLButtonElement).disabled).toBe(false);
	});

	it("hands the page back to the editor without touching the run", () => {
		const { onBackToEditor } = renderToolbar(graphWorkflowRunSummary({ status: "Completed" }));

		fireEvent.click(screen.getByTestId("graph-workflow-run-toolbar-back"));

		expect(onBackToEditor).toHaveBeenCalledTimes(1);
	});
});
