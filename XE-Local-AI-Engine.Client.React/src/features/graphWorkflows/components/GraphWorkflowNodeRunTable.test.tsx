// @vitest-environment jsdom

// The table is the authoritative path through a run: the canvas is a pointer surface, this is what a keyboard and a
// screen reader walk. So the row's numbers — status, attempt, duration — are pinned here rather than on the cards.

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { GraphWorkflowNodeRunTable } from "@/features/graphWorkflows/components/GraphWorkflowNodeRunTable";
import { graphWorkflowRun, makeNodeRun } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

const nodeRuns = graphWorkflowRun().nodeRuns ?? [];

describe("GraphWorkflowNodeRunTable", () => {
	afterEach(() => {
		cleanup();
	});

	it("renders one row per node run, with its status, attempt and duration", () => {
		renderWithProviders(<GraphWorkflowNodeRunTable nodeRuns={nodeRuns} onSelectNode={vi.fn()} />);

		expect(screen.getAllByTestId(/^graph-workflow-node-row-/)).toHaveLength(nodeRuns.length);
		expect(screen.getByTestId("graph-workflow-node-status-analyze").textContent).toBe("Succeeded");
		expect(screen.getByTestId("graph-workflow-node-attempt-lookup").textContent).toBe("3");
		// The fixture's Agent node ran from 1_700_000_200_000 to 1_700_000_210_000.
		expect(screen.getByTestId("graph-workflow-node-duration-analyze").textContent).toBe("10s");
	});

	it("leaves the duration open while a node is still running", () => {
		renderWithProviders(
			<GraphWorkflowNodeRunTable
				nodeRuns={[makeNodeRun({ id: "nr-analyze", nodeKey: "analyze", status: "Running", completedAtUtc: null })]}
				onSelectNode={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("graph-workflow-node-duration-analyze").textContent).toBe("—");
	});

	it("names the failure class of a failed node, and stays silent about None", () => {
		renderWithProviders(<GraphWorkflowNodeRunTable nodeRuns={nodeRuns} onSelectNode={vi.fn()} />);

		expect(screen.getByTestId("graph-workflow-node-failure-lookup").textContent).toBe("Out of attempts");
		expect(screen.queryByTestId("graph-workflow-node-failure-analyze")).toBeNull();
	});

	it("marks the node that is holding the run up for a decision", () => {
		renderWithProviders(<GraphWorkflowNodeRunTable nodeRuns={nodeRuns} onSelectNode={vi.fn()} />);

		expect(screen.getByTestId("graph-workflow-node-pending-review").textContent).toBe("needs your decision");
		expect(screen.queryByTestId("graph-workflow-node-pending-analyze")).toBeNull();
	});

	it("orders the rows by start, and puts a node that never started last", () => {
		renderWithProviders(
			<GraphWorkflowNodeRunTable
				nodeRuns={[
					makeNodeRun({ id: "nr-done", nodeKey: "done", startedAtUtc: null, completedAtUtc: null }),
					makeNodeRun({ id: "nr-analyze", nodeKey: "analyze", startedAtUtc: 1_700_000_300_000 }),
					makeNodeRun({ id: "nr-start", nodeKey: "start", startedAtUtc: 1_700_000_200_000 }),
				]}
				onSelectNode={vi.fn()}
			/>,
		);

		expect(screen.getAllByTestId(/^graph-workflow-node-row-/).map((row) => row.getAttribute("data-testid"))).toEqual([
			"graph-workflow-node-row-start",
			"graph-workflow-node-row-analyze",
			"graph-workflow-node-row-done",
		]);
	});

	it("selects a node by its key, from the row and from the control a keyboard reaches", () => {
		const onSelectNode = vi.fn();
		renderWithProviders(<GraphWorkflowNodeRunTable nodeRuns={nodeRuns} onSelectNode={onSelectNode} />);

		fireEvent.click(screen.getByTestId("graph-workflow-node-row-review"));
		expect(onSelectNode).toHaveBeenCalledWith("review");

		fireEvent.click(screen.getByTestId("graph-workflow-node-select-lookup"));
		expect(onSelectNode).toHaveBeenLastCalledWith("lookup");
	});

	it("says the run has no nodes rather than drawing an empty table", () => {
		renderWithProviders(<GraphWorkflowNodeRunTable nodeRuns={[]} onSelectNode={vi.fn()} />);

		expect(screen.getByTestId("graph-workflow-node-runs-empty")).toBeDefined();
		expect(screen.queryByTestId("graph-workflow-node-run-table")).toBeNull();
	});
});
