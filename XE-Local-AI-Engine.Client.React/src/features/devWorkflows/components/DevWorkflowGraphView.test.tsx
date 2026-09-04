// @vitest-environment jsdom

// The O9 honesty rules the node-run table already guards, asserted a second time on the canvas — a card is a second
// place a `Queued` node could be made to look like a running one.
//
// React Flow renders into a measured container and jsdom reports 0×0, which suppresses the viewport entirely (see the
// same note in `features/preview/components/WorkflowCanvas.test.tsx`), so no card would ever paint through a real
// `<ReactFlow>`. The mock below is the minimum that keeps the units under test real: it renders each node through the
// registered `nodeTypes` component with the data the mapper built, and wires the click the view actually listens for.

import { cleanup, fireEvent, screen } from "@testing-library/react";
import type { Edge, Node, NodeTypes } from "@xyflow/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { DevWorkflowGraphView } from "@/features/devWorkflows/components/DevWorkflowGraphView";
import {
	DEV_WORKFLOW_MAX_RENDERED_NODES,
	toDevWorkflowDefinitionCanvasGraph,
} from "@/features/devWorkflows/models/DevWorkflowGraphModels";
import type { DevWorkflowRunResponse } from "@/features/devWorkflows/models/DevWorkflowModels";
import { devWorkflowNodeRunSummary, devWorkflowRun } from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

interface ReactFlowMockProps {
	readonly nodes: readonly Node[];
	readonly edges: readonly Edge[];
	readonly nodeTypes: NodeTypes;
	readonly minZoom?: number;
	readonly onNodeClick?: (event: React.MouseEvent, node: Node) => void;
	readonly children?: React.ReactNode;
}

vi.mock("@xyflow/react", () => ({
	MarkerType: { ArrowClosed: "arrowclosed" },
	Position: { Left: "left", Right: "right" },
	Handle: () => null,
	Background: () => null,
	Controls: () => null,
	ReactFlowProvider: ({ children }: { children: React.ReactNode }) => children,
	useReactFlow: () => ({ fitView: () => Promise.resolve(true) }),
	ReactFlow: ({ nodes, edges, nodeTypes, minZoom, onNodeClick, children }: ReactFlowMockProps) => (
		<div data-testid="react-flow" data-edges={edges.map((edge) => edge.id).join(" ")} data-min-zoom={minZoom}>
			{nodes.map((node) => {
				const NodeComponent = nodeTypes[node.type ?? ""];
				return (
					<button
						key={node.id}
						type="button"
						onClick={(event) => onNodeClick?.(event, node)}
						data-testid={`react-flow-node-${node.id}`}
					>
						{NodeComponent ? (
							<NodeComponent
								id={node.id}
								type={node.type ?? ""}
								data={node.data}
								selected={node.selected ?? false}
								dragging={false}
								draggable={false}
								selectable={node.selectable ?? true}
								deletable={false}
								isConnectable={false}
								positionAbsoluteX={node.position.x}
								positionAbsoluteY={node.position.y}
								zIndex={0}
							/>
						) : null}
					</button>
				);
			})}
			{children}
		</div>
	),
}));

/** research → plan → approve, edges by node key exactly as the pinned graph carries them. */
function chainRun(nodes: DevWorkflowRunResponse["nodes"]): DevWorkflowRunResponse {
	return devWorkflowRun({
		graph: {
			schemaVersion: 1,
			nodes: [],
			edges: [
				{ from: "research", to: "plan" },
				{ from: "plan", to: "approve" },
			],
		},
		nodes,
	});
}

function card(nodeRunId: string): HTMLElement {
	return screen.getByTestId(`dev-workflow-graph-node-${nodeRunId}`);
}

/** Mantine's Loader is the only progress indicator a card can carry; the status badge renders it as its left section. */
function hasProgressIndicator(element: HTMLElement): boolean {
	return element.querySelector(".mantine-Loader-root") !== null;
}

describe("DevWorkflowGraphView", () => {
	afterEach(() => {
		cleanup();
	});

	it("draws one card per node-run, labelled and typed from the node-run row", () => {
		renderWithProviders(
			<DevWorkflowGraphView
				run={chainRun([
					devWorkflowNodeRunSummary({ id: "node-research", nodeKey: "research", label: "Research" }),
					devWorkflowNodeRunSummary({
						id: "node-plan",
						nodeKey: "plan",
						nodeType: "HumanGate",
						label: "Approve the plan",
						status: "WaitingForApproval",
					}),
				])}
				onSelect={vi.fn()}
			/>,
		);

		expect(card("node-research").textContent).toContain("Research");
		expect(card("node-plan").textContent).toContain("Approve the plan");
		expect(card("node-plan").textContent).toContain("Approval");
	});

	it("gives a Queued card no progress indicator, and a Running one", () => {
		renderWithProviders(
			<DevWorkflowGraphView
				run={chainRun([
					devWorkflowNodeRunSummary({
						id: "node-research",
						nodeKey: "research",
						status: "Queued",
						queueReason: "awaiting-agent-slot",
						startedAtUtc: null,
					}),
					devWorkflowNodeRunSummary({ id: "node-plan", nodeKey: "plan", status: "Running" }),
				])}
				onSelect={vi.fn()}
			/>,
		);

		expect(hasProgressIndicator(card("node-research"))).toBe(false);
		expect(hasProgressIndicator(card("node-plan"))).toBe(true);
	});

	it("renders a Blocked card as needs-intervention rather than as a passive wait", () => {
		renderWithProviders(
			<DevWorkflowGraphView
				run={chainRun([
					devWorkflowNodeRunSummary({ id: "node-research", nodeKey: "research", status: "Blocked", attempt: 3 }),
				])}
				onSelect={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("dev-workflow-graph-node-intervention-node-research").textContent).toBe(
			"needs your intervention",
		);
		expect(hasProgressIndicator(card("node-research"))).toBe(false);
		expect(screen.getByTestId("dev-workflow-graph-node-attempt-node-research").textContent).toBe("attempt 3 of 3");
	});

	it("carries the operator-retry count onto the card, so the canvas names the declared cap too", () => {
		renderWithProviders(
			<DevWorkflowGraphView
				run={chainRun([
					devWorkflowNodeRunSummary({
						id: "node-research",
						nodeKey: "research",
						status: "Blocked",
						attempt: 4,
						maxAttempts: 4,
						operatorRetries: 1,
					}),
				])}
				onSelect={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("dev-workflow-graph-node-attempt-node-research").textContent).toBe(
			"attempt 4 of 4 (cap 3, +1 from an operator retry)",
		);
	});

	it("says which part of a decomposition a generated card is, and stays quiet about a group of one", () => {
		renderWithProviders(
			<DevWorkflowGraphView
				run={chainRun([
					...[1, 2].map((index) =>
						devWorkflowNodeRunSummary({
							id: `node-implement-${index}`,
							nodeKey: `implement#${index}`,
							isMaterialized: true,
							materializedFromNodeKey: "decompose",
							materializationIndex: index,
							materializationCount: 2,
						}),
					),
					devWorkflowNodeRunSummary({
						id: "node-verify",
						nodeKey: "verify#1",
						isMaterialized: true,
						materializedFromNodeKey: "review",
						materializationIndex: 1,
						materializationCount: 1,
					}),
				])}
				onSelect={vi.fn()}
			/>,
		);

		// Both numbers are the SERVER's: the index is already 1-based (C2) and the count is the group size the runtime
		// computed. Adding one to the index is what made a decomposition of one read "generated · 2 of 2" on live
		// hardware; counting rows client-side is what made a two-child clone read "of 4".
		expect(screen.getByTestId("dev-workflow-graph-node-materialized-node-implement-1").textContent).toBe(
			"generated · 1 of 2",
		);
		expect(screen.getByTestId("dev-workflow-graph-node-materialized-node-implement-2").textContent).toBe(
			"generated · 2 of 2",
		);
		expect(screen.getByTestId("dev-workflow-graph-node-materialized-node-verify").textContent).toBe("generated");
	});

	it("badges a cloned subtree by its CHILD, so two children of a two-node template never read 3 or 4", () => {
		renderWithProviders(
			<DevWorkflowGraphView
				run={chainRun(
					[1, 2].flatMap((child) =>
						["implement", "validate"].map((step) =>
							devWorkflowNodeRunSummary({
								id: `node-${step}-${child}`,
								nodeKey: `${step}#child-${child}`,
								isMaterialized: true,
								materializedFromNodeKey: "decompose",
								materializationIndex: child,
								// The runtime counts the CHILDREN of the decomposition; four rows is what a cloned
								// two-node subtree looks like, and the count must not follow the row count.
								materializationCount: 2,
							}),
						),
					),
				)}
				onSelect={vi.fn()}
			/>,
		);

		expect(
			[1, 2]
				.flatMap((child) => ["implement", "validate"].map((step) => `node-${step}-${child}`))
				.map((id) => screen.getByTestId(`dev-workflow-graph-node-materialized-${id}`).textContent),
		).toEqual(["generated · 1 of 2", "generated · 1 of 2", "generated · 2 of 2", "generated · 2 of 2"]);
	});

	it("badges an apply node as one, because a validation node's card is otherwise identical", () => {
		renderWithProviders(
			<DevWorkflowGraphView
				graph={toDevWorkflowDefinitionCanvasGraph({
					schemaVersion: 1,
					nodes: [
						{ nodeKey: "integrate", nodeType: "Tool", label: "Integrate", toolMode: "Apply" },
						{ nodeKey: "validate", nodeType: "Tool", label: "Validate" },
					],
					edges: [{ from: "validate", to: "integrate" }],
				})}
				onSelect={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("dev-workflow-graph-node-apply-integrate").textContent).toBe("applies patches");
		expect(screen.queryByTestId("dev-workflow-graph-node-apply-validate")).toBeNull();
	});

	it("selects a node-run when its card is clicked, the same change a table row makes", () => {
		const onSelect = vi.fn();
		renderWithProviders(
			<DevWorkflowGraphView
				run={chainRun([devWorkflowNodeRunSummary({ id: "node-research", nodeKey: "research" })])}
				onSelect={onSelect}
			/>,
		);

		fireEvent.click(screen.getByTestId("react-flow-node-node-research"));

		expect(onSelect).toHaveBeenCalledWith("node-research");
	});

	it("draws the synthetic start and end anchors, and never selects one", () => {
		const onSelect = vi.fn();
		renderWithProviders(
			<DevWorkflowGraphView
				// Both edges of this definition are drawable, so `plan` really is the terminal — an End anchor here is
				// the truth, unlike the dangling case the mapper's own test covers.
				run={devWorkflowRun({
					graph: { schemaVersion: 1, nodes: [], edges: [{ from: "research", to: "plan" }] },
					nodes: [
						devWorkflowNodeRunSummary({ id: "node-research", nodeKey: "research" }),
						devWorkflowNodeRunSummary({ id: "node-plan", nodeKey: "plan" }),
					],
				})}
				onSelect={onSelect}
			/>,
		);

		expect(screen.getByTestId("dev-workflow-graph-anchor-start-research").textContent).toBe("Start");
		expect(screen.getByTestId("dev-workflow-graph-anchor-end-plan").textContent).toBe("End");

		fireEvent.click(screen.getByTestId("react-flow-node-dev-workflow-anchor-start-node-research"));
		expect(onSelect).not.toHaveBeenCalled();
	});

	it("shows a banner instead of a canvas for a run past the render cap", () => {
		renderWithProviders(
			<DevWorkflowGraphView
				run={devWorkflowRun({
					nodes: Array.from({ length: DEV_WORKFLOW_MAX_RENDERED_NODES + 1 }, (_, index) =>
						devWorkflowNodeRunSummary({ id: `node-${index}`, nodeKey: `key-${index}` }),
					),
				})}
				onSelect={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("dev-workflow-graph-over-cap").textContent).toContain("201");
		expect(screen.queryByTestId("react-flow")).toBeNull();
	});

	it("lets the viewport zoom out far enough to frame a whole run", () => {
		renderWithProviders(
			<DevWorkflowGraphView
				run={chainRun([devWorkflowNodeRunSummary({ id: "node-research", nodeKey: "research" })])}
				onSelect={vi.fn()}
			/>,
		);

		// React Flow's default minZoom (0.5) clamps fitView, and a clamped fit opens clipped with Zoom Out disabled.
		expect(screen.getByTestId("react-flow").getAttribute("data-min-zoom")).toBe("0.1");
	});

	it("says the run has no node-runs yet rather than drawing an empty canvas", () => {
		renderWithProviders(<DevWorkflowGraphView run={devWorkflowRun({ nodes: [] })} onSelect={vi.fn()} />);

		expect(screen.getByTestId("dev-workflow-graph-empty")).toBeDefined();
		expect(screen.queryByTestId("react-flow")).toBeNull();
	});
});
