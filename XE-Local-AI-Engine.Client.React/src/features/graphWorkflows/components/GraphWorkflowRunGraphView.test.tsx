// @vitest-environment jsdom

// React Flow renders into a measured container and jsdom reports 0×0, which suppresses the viewport entirely (the same
// note stands in `devWorkflows/components/DevWorkflowGraphView.test.tsx`), so no card would ever paint through a real
// `<ReactFlow>`. The mock below is the minimum that keeps the units under test real: it renders each node through the
// registered `nodeTypes` component, exposes the read-only props as attributes, and wires the two clicks the view
// listens for. `fitView` is a shared spy, because "a status tick does not re-frame the viewport" is a CALL COUNT.

import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { Edge, Node, NodeTypes } from "@xyflow/react";
import type { ReactElement, ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { GraphWorkflowRunGraphView } from "@/features/graphWorkflows/components/GraphWorkflowRunGraphView";
import { GRAPH_WORKFLOW_MAX_RENDERED_NODES } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { toGraphWorkflowRunCanvas } from "@/features/graphWorkflows/models/GraphWorkflowRunGraph";
import {
	eightNodeGraph,
	graphWorkflowDefinition,
	graphWorkflowRun,
	graphWorkflowRunSummary,
	graphWorkflowTestGraphHash,
	makeNodeRun,
} from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

const fitView = vi.hoisted(() => vi.fn(() => Promise.resolve(true)));

interface ReactFlowMockProps {
	readonly nodes: readonly Node[];
	readonly edges: readonly Edge[];
	readonly nodeTypes: NodeTypes;
	readonly minZoom?: number;
	readonly nodesDraggable?: boolean;
	readonly nodesConnectable?: boolean;
	readonly edgesFocusable?: boolean;
	readonly onNodeClick?: (event: React.MouseEvent, node: Node) => void;
	readonly onPaneClick?: () => void;
	readonly children?: ReactNode;
}

vi.mock("@xyflow/react", () => ({
	MarkerType: { ArrowClosed: "arrowclosed" },
	Position: { Left: "left", Right: "right" },
	Handle: () => null,
	Background: () => null,
	Controls: () => null,
	ReactFlowProvider: ({ children }: { children: ReactNode }) => children,
	useReactFlow: () => ({ fitView }),
	ReactFlow: ({
		nodes,
		edges,
		nodeTypes,
		minZoom,
		nodesDraggable,
		nodesConnectable,
		edgesFocusable,
		onNodeClick,
		onPaneClick,
		children,
	}: ReactFlowMockProps) => (
		<div
			data-testid="react-flow"
			data-edges={edges.map((edge) => edge.id).join(" ")}
			data-min-zoom={minZoom}
			data-nodes-draggable={String(nodesDraggable)}
			data-nodes-connectable={String(nodesConnectable)}
			data-edges-focusable={String(edgesFocusable)}
		>
			<button type="button" onClick={() => onPaneClick?.()} data-testid="react-flow-pane" />
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

const definitionGraph = { graph: eightNodeGraph, graphHash: graphWorkflowTestGraphHash };

/** `render` with the app's provider stack as the WRAPPER, so a `rerender` keeps it — which the fitView test needs. */
function renderView(ui: ReactElement) {
	const { wrapper } = createProvidersWrapper();
	return render(ui, { wrapper });
}

/** The run of the eight-node definition, drawn on the definition it actually ran (the hashes agree). */
function matchedCanvas(nodeRuns = graphWorkflowRun().nodeRuns ?? []) {
	return toGraphWorkflowRunCanvas({ run: graphWorkflowRunSummary(), nodeRuns, definitionGraph });
}

describe("GraphWorkflowRunGraphView", () => {
	afterEach(() => {
		cleanup();
		fitView.mockClear();
	});

	it("draws one read-only card per node of the pinned graph, with its node run's status", () => {
		renderView(<GraphWorkflowRunGraphView canvas={matchedCanvas()} onSelectNode={vi.fn()} />);

		expect(screen.getByTestId("graph-workflow-run-node-analyze").textContent).toContain("Analyze");
		expect(screen.getByTestId("graph-workflow-run-node-status-analyze").textContent).toBe("Succeeded");
		expect(screen.getByTestId("graph-workflow-run-node-status-review").textContent).toBe("Waiting for a decision");
	});

	it("sets the read-only props: nothing on this canvas drags, connects or takes edge focus", () => {
		renderView(<GraphWorkflowRunGraphView canvas={matchedCanvas()} onSelectNode={vi.fn()} />);

		const flow = screen.getByTestId("react-flow");
		expect(flow.getAttribute("data-nodes-draggable")).toBe("false");
		expect(flow.getAttribute("data-nodes-connectable")).toBe("false");
		expect(flow.getAttribute("data-edges-focusable")).toBe("false");
		// React Flow's default minZoom (0.5) clamps fitView, and a clamped fit opens the graph clipped.
		expect(flow.getAttribute("data-min-zoom")).toBe("0.1");
	});

	it("shows the attempt only once a node has been retried", () => {
		renderView(<GraphWorkflowRunGraphView canvas={matchedCanvas()} onSelectNode={vi.fn()} />);

		// The fixture's Tool node is on its third attempt; the Agent node succeeded first time.
		expect(screen.getByTestId("graph-workflow-run-node-attempt-lookup").textContent).toBe("attempt 3");
		expect(screen.queryByTestId("graph-workflow-run-node-attempt-analyze")).toBeNull();
	});

	it("selects a node when its card is clicked, and clears the selection on the pane", () => {
		const onSelectNode = vi.fn();
		renderView(<GraphWorkflowRunGraphView canvas={matchedCanvas()} onSelectNode={onSelectNode} />);

		fireEvent.click(screen.getByTestId("react-flow-node-review"));
		expect(onSelectNode).toHaveBeenCalledWith("review");

		fireEvent.click(screen.getByTestId("react-flow-pane"));
		expect(onSelectNode).toHaveBeenLastCalledWith(undefined);
	});

	it("re-frames the viewport when the graph's shape changes, and NOT on a status tick", () => {
		const running = graphWorkflowRun().nodeRuns ?? [];
		const { rerender } = renderView(
			<GraphWorkflowRunGraphView canvas={matchedCanvas(running)} onSelectNode={vi.fn()} />,
		);
		expect(fitView).toHaveBeenCalledTimes(1);

		// A status tick: same nodes, same edges, same graph hash — an operator's viewport must not jump under them.
		const ticked = running.map((nodeRun) => (nodeRun.nodeKey === "review" ? { ...nodeRun, status: "Succeeded" } : nodeRun));
		rerender(<GraphWorkflowRunGraphView canvas={matchedCanvas(ticked)} onSelectNode={vi.fn()} />);
		expect(fitView).toHaveBeenCalledTimes(1);

		// A different graph is a different shape, and that IS worth re-framing.
		rerender(
			<GraphWorkflowRunGraphView
				canvas={toGraphWorkflowRunCanvas({
					run: graphWorkflowRunSummary({ graphHash: "sha256:another" }),
					nodeRuns: [makeNodeRun({ id: "nr-start", nodeKey: "start", kind: "Start" })],
					definitionGraph: { graph: graphWorkflowDefinition().graph, graphHash: "sha256:another" },
				})}
				onSelectNode={vi.fn()}
			/>,
		);
		expect(fitView).toHaveBeenCalledTimes(2);
	});

	it("warns that the definition changed since the run, and draws the nodes without edges", () => {
		const canvas = toGraphWorkflowRunCanvas({
			// The definition was saved again after the run started: its edges are not the routing this run took.
			run: graphWorkflowRunSummary({ graphHash: "sha256:the-run-was-pinned-to-this" }),
			nodeRuns: graphWorkflowRun().nodeRuns ?? [],
			definitionGraph,
		});
		renderView(<GraphWorkflowRunGraphView canvas={canvas} onSelectNode={vi.fn()} />);

		expect(screen.getByTestId("graph-workflow-run-graph-mismatch").textContent).toContain("nodes only");
		expect(screen.getByTestId("react-flow").getAttribute("data-edges")).toBe("");
		expect(screen.getByTestId("graph-workflow-run-node-analyze")).toBeDefined();
	});

	it("shows a banner instead of a canvas for a run past the render cap", () => {
		const canvas = toGraphWorkflowRunCanvas({
			run: graphWorkflowRunSummary({ graphHash: "sha256:over-cap" }),
			nodeRuns: Array.from({ length: GRAPH_WORKFLOW_MAX_RENDERED_NODES + 1 }, (_, index) =>
				makeNodeRun({ id: `nr-${index}`, nodeKey: `key-${index}` }),
			),
			definitionGraph: { graph: undefined, graphHash: undefined },
		});
		renderView(<GraphWorkflowRunGraphView canvas={canvas} onSelectNode={vi.fn()} />);

		expect(screen.getByTestId("graph-workflow-run-graph-over-cap").textContent).toContain("201");
		expect(screen.queryByTestId("react-flow")).toBeNull();
	});

	it("says the run has no nodes rather than drawing an empty canvas", () => {
		const canvas = toGraphWorkflowRunCanvas({ run: graphWorkflowRunSummary(), nodeRuns: [] });
		renderView(<GraphWorkflowRunGraphView canvas={canvas} onSelectNode={vi.fn()} />);

		expect(screen.getByTestId("graph-workflow-run-graph-empty")).toBeDefined();
		expect(screen.queryByTestId("react-flow")).toBeNull();
	});
});
