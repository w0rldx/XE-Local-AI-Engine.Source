// @vitest-environment jsdom

// The O9 honesty regression tests live here, because the table is the execution view in Slice A0 and it is where a
// dishonest status would first be seen. Two of them guard rulings that were WRONG in an earlier draft of the plan:
// a `Queued` node must not animate (it is waiting for a slot another node holds), and `Blocked` must read as
// needs-intervention rather than as a passive dependency wait (Y20) — rendered the old way, a run that had stopped
// dead would have looked like a run quietly making progress.

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { DevWorkflowNodeRunTable } from "@/features/devWorkflows/components/DevWorkflowNodeRunTable";
import { devWorkflowNodeRunSummary, devWorkflowTestIds } from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

const reducedMotion = vi.hoisted(() => ({ value: false }));

vi.mock("framer-motion", async (importOriginal) => ({
	...(await importOriginal<typeof import("framer-motion")>()),
	useReducedMotion: () => reducedMotion.value,
}));

function statusCell(nodeRunId: string): HTMLElement {
	const badge = screen.getByTestId(`dev-workflow-node-status-${nodeRunId}`);
	const cell = badge.closest("td");
	if (!cell) {
		throw new Error("status badge is not inside a table cell");
	}
	return cell;
}

/** Mantine's Loader is the only progress indicator these rows can carry; the badge renders it as its left section. */
function hasProgressIndicator(cell: HTMLElement): boolean {
	return cell.querySelector(".mantine-Loader-root") !== null;
}

describe("DevWorkflowNodeRunTable", () => {
	afterEach(() => {
		reducedMotion.value = false;
		cleanup();
	});

	it("renders one row per node-run, ordered by sequence", () => {
		renderWithProviders(
			<DevWorkflowNodeRunTable
				nodes={[
					devWorkflowNodeRunSummary({ id: "node-b", nodeKey: "plan", label: "Plan", sequence: 5, status: "Pending" }),
					devWorkflowNodeRunSummary({ id: "node-a", nodeKey: "research", label: "Research", sequence: 2 }),
				]}
				onSelect={vi.fn()}
			/>,
		);

		const rows = screen.getAllByTestId(/^dev-workflow-node-row-/);
		expect(rows.map((row) => row.getAttribute("data-testid"))).toEqual([
			"dev-workflow-node-row-node-a",
			"dev-workflow-node-row-node-b",
		]);
	});

	it("renders a Queued row with its translated queue reason and NO progress indicator", () => {
		renderWithProviders(
			<DevWorkflowNodeRunTable
				nodes={[
					devWorkflowNodeRunSummary({
						status: "Queued",
						queueReason: "awaiting-agent-slot",
						queuedAtUtc: Date.now() - 45_000,
						startedAtUtc: null,
					}),
				]}
				onSelect={vi.fn()}
			/>,
		);

		const detail = screen.getByTestId(`dev-workflow-node-detail-${devWorkflowTestIds.nodeRun}`);
		// X7: admission is the work-session supervisor's single agent slot, so the copy says agent slot, not "model process".
		expect(detail.textContent).toContain("waiting for the agent slot");
		expect(detail.textContent).toContain("queued for 45s");
		expect(hasProgressIndicator(statusCell(devWorkflowTestIds.nodeRun))).toBe(false);
	});

	it("falls back to a generic queued line for a queue reason a newer server invented", () => {
		renderWithProviders(
			<DevWorkflowNodeRunTable
				nodes={[devWorkflowNodeRunSummary({ status: "Queued", queueReason: "awaiting-something-new", queuedAtUtc: null })]}
				onSelect={vi.fn()}
			/>,
		);

		const detail = screen.getByTestId(`dev-workflow-node-detail-${devWorkflowTestIds.nodeRun}`);
		expect(detail.textContent).toBe("queued");
		expect(detail.textContent).not.toContain("awaiting-something-new");
	});

	it("renders a progress indicator for a Running row", () => {
		renderWithProviders(<DevWorkflowNodeRunTable nodes={[devWorkflowNodeRunSummary({ status: "Running" })]} onSelect={vi.fn()} />);

		expect(hasProgressIndicator(statusCell(devWorkflowTestIds.nodeRun))).toBe(true);
	});

	it("renders no progress indicator for a Running row when the operator asked for reduced motion", () => {
		reducedMotion.value = true;
		renderWithProviders(<DevWorkflowNodeRunTable nodes={[devWorkflowNodeRunSummary({ status: "Running" })]} onSelect={vi.fn()} />);

		expect(hasProgressIndicator(statusCell(devWorkflowTestIds.nodeRun))).toBe(false);
	});

	it("renders a Blocked row as needs-intervention, not as a passive wait", () => {
		renderWithProviders(
			<DevWorkflowNodeRunTable
				nodes={[devWorkflowNodeRunSummary({ status: "Blocked", attempt: 3, maxAttempts: 3 })]}
				onSelect={vi.fn()}
			/>,
		);

		expect(screen.getByTestId(`dev-workflow-node-detail-${devWorkflowTestIds.nodeRun}`).textContent).toBe(
			"needs your intervention",
		);
		expect(screen.getByTestId(`dev-workflow-node-status-${devWorkflowTestIds.nodeRun}`).textContent).toBe("Needs intervention");
		expect(screen.getByTestId(`dev-workflow-node-attempt-${devWorkflowTestIds.nodeRun}`).textContent).toBe("attempt 3 of 3");
	});

	it("names the nodes a Pending row is waiting on, by label rather than by node key", () => {
		renderWithProviders(
			<DevWorkflowNodeRunTable
				nodes={[
					devWorkflowNodeRunSummary({ id: "node-a", nodeKey: "research", label: "Research", sequence: 1, status: "Succeeded" }),
					devWorkflowNodeRunSummary({
						id: "node-b",
						nodeKey: "plan",
						label: "Draft the plan",
						sequence: 2,
						status: "Pending",
						startedAtUtc: null,
						waitingOnNodeKeys: ["research"],
					}),
				]}
				onSelect={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("dev-workflow-node-detail-node-b").textContent).toBe("waiting on Research");
	});

	it("says a Pending row with no dependency has simply not been reached", () => {
		renderWithProviders(
			<DevWorkflowNodeRunTable
				nodes={[devWorkflowNodeRunSummary({ status: "Pending", startedAtUtc: null, waitingOnNodeKeys: null })]}
				onSelect={vi.fn()}
			/>,
		);

		expect(screen.getByTestId(`dev-workflow-node-detail-${devWorkflowTestIds.nodeRun}`).textContent).toBe("not reached yet");
	});

	it("selects a node-run when its row is clicked", () => {
		const onSelect = vi.fn();
		renderWithProviders(<DevWorkflowNodeRunTable nodes={[devWorkflowNodeRunSummary()]} onSelect={onSelect} />);

		fireEvent.click(screen.getByTestId(`dev-workflow-node-row-${devWorkflowTestIds.nodeRun}`));

		expect(onSelect).toHaveBeenCalledWith(devWorkflowTestIds.nodeRun);
	});

	it("puts a real focusable control in the row, because a bare row click is unreachable by keyboard", () => {
		const onSelect = vi.fn();
		renderWithProviders(<DevWorkflowNodeRunTable nodes={[devWorkflowNodeRunSummary({ label: "Research" })]} onSelect={onSelect} />);

		// This table is A0's ONLY execution view, so every node has to be reachable without a pointer.
		const control = screen.getByRole("button", { name: "Research" });
		control.focus();
		expect(document.activeElement).toBe(control);

		fireEvent.click(control);
		expect(onSelect).toHaveBeenCalledWith(devWorkflowTestIds.nodeRun);
	});

	it("prints what a settled row's last attempt cost", () => {
		renderWithProviders(
			<DevWorkflowNodeRunTable
				nodes={[devWorkflowNodeRunSummary({ status: "Succeeded", inputTokens: 1200, outputTokens: 340, toolCalls: 7 })]}
				onSelect={vi.fn()}
			/>,
		);

		expect(screen.getByTestId(`dev-workflow-node-cost-${devWorkflowTestIds.nodeRun}`).textContent).toBe("1,200 / 340 tok · 7 tool calls");
	});

	// One tool call is not "1 tool calls". The key is a plural family, so it needs both forms in every locale or
	// i18next falls back to the base key and the row reads as a bug.
	it("counts a single tool call in the singular", () => {
		renderWithProviders(
			<DevWorkflowNodeRunTable
				nodes={[devWorkflowNodeRunSummary({ status: "Succeeded", inputTokens: null, outputTokens: null, toolCalls: 1 })]}
				onSelect={vi.fn()}
			/>,
		);

		expect(screen.getByTestId(`dev-workflow-node-cost-${devWorkflowTestIds.nodeRun}`).textContent).toBe("1 tool call");
	});

	// A structural node, a row from before the columns existed, and a collection that could not run all read the same
	// way. Zero would be a lie about all three: it claims the attempt was free.
	it("prints a dash, never a zero, for a row that reported no cost", () => {
		renderWithProviders(
			<DevWorkflowNodeRunTable
				nodes={[devWorkflowNodeRunSummary({ status: "Succeeded", inputTokens: null, outputTokens: null, toolCalls: null })]}
				onSelect={vi.fn()}
			/>,
		);

		expect(screen.getByTestId(`dev-workflow-node-cost-${devWorkflowTestIds.nodeRun}`).textContent).toBe("—");
	});

	it("prints the half it has when only one side was measured", () => {
		renderWithProviders(
			<DevWorkflowNodeRunTable
				nodes={[devWorkflowNodeRunSummary({ status: "Succeeded", inputTokens: null, outputTokens: 340, toolCalls: null })]}
				onSelect={vi.fn()}
			/>,
		);

		expect(screen.getByTestId(`dev-workflow-node-cost-${devWorkflowTestIds.nodeRun}`).textContent).toBe("– / 340 tok");
	});

	it("renders an empty state rather than an empty table", () => {
		renderWithProviders(<DevWorkflowNodeRunTable nodes={[]} onSelect={vi.fn()} />);

		expect(screen.getByTestId("dev-workflow-node-runs-empty")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-node-run-table")).toBeNull();
	});
});
