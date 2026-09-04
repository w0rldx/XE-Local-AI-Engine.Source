// @vitest-environment jsdom

// The run header's counts, and the one row that must not be in them: a zero-task decomposition seeds its template's
// checks as `Succeeded` (D12) so the join behind them lets the apply through, but those rows stand for work that did
// not happen. Counting them as done reports a run that decomposed into nothing as having completed a validation.

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { DevWorkflowRunSummaryPanel } from "@/features/devWorkflows/components/DevWorkflowRunSummaryPanel";
import { devWorkflowNodeRunSummary } from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

function renderPanel(nodes: Parameters<typeof DevWorkflowRunSummaryPanel>[0]["nodes"]) {
	return renderWithProviders(
		<DevWorkflowRunSummaryPanel
			runs={[]}
			selectedRunId="run"
			nodes={nodes}
			pendingDecisionCount={0}
			startableDefinitions={[]}
			selectedDefinitionId={null}
			onSelectDefinition={() => undefined}
			isStarting={false}
			onSelectRun={() => undefined}
			onStartRun={() => undefined}
		/>,
	);
}

describe("DevWorkflowRunSummaryPanel", () => {
	afterEach(() => {
		cleanup();
	});

	it("counts a real pass as done", () => {
		renderPanel([
			devWorkflowNodeRunSummary({ id: "a", status: "Succeeded" }),
			devWorkflowNodeRunSummary({ id: "b", status: "Running" }),
		]);

		expect(screen.getByTestId("dev-workflow-progress-counts").textContent).toBe("1 running · 0 queued · 1/2 done");
	});

	it("leaves a check that had nothing to check out of both the done count and the total", () => {
		renderPanel([
			devWorkflowNodeRunSummary({ id: "a", status: "Succeeded" }),
			devWorkflowNodeRunSummary({ id: "b", status: "Running" }),
			devWorkflowNodeRunSummary({ id: "c", status: "Succeeded", validationNotApplicable: true }),
		]);

		expect(screen.getByTestId("dev-workflow-progress-counts").textContent).toBe("1 running · 0 queued · 1/2 done");
	});
});
