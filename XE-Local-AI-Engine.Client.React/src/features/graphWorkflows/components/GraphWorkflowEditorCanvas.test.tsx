// @vitest-environment jsdom

// React Flow renders into a measured container and jsdom reports 0×0, so a real `<ReactFlow>` paints no viewport and
// no card — the same note `features/preview/components/WorkflowCanvas.test.tsx` carries. The mock below keeps
// everything else real (the layout helpers, `applyNodeChanges`, the editor hook) and captures the props the canvas
// hands React Flow, which is where the units under test actually land: the node and edge arrays, and the callbacks a
// pointer would otherwise have to reach.

import { act, cleanup, fireEvent, screen } from "@testing-library/react";
import type { Edge, Node, ReactFlowProps } from "@xyflow/react";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { GraphWorkflowEditorCanvas } from "@/features/graphWorkflows/components/GraphWorkflowEditorCanvas";
import { useGraphWorkflowEditor } from "@/features/graphWorkflows/hooks/useGraphWorkflowEditor";
import {
	type GraphWorkflowCanvasEdge,
	type GraphWorkflowCanvasNode,
	graphWorkflowNodeTypeByKind,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { GRAPH_WORKFLOW_MAX_NODES, type GraphWorkflowGraph } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import type { GraphWorkflowGraphIssue } from "@/features/graphWorkflows/models/GraphWorkflowValidation";
import { eightNodeGraph } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

// `dropPosition` is what the mocked `screenToFlowPosition` answers: jsdom has no layout and its synthetic drop event
// carries no usable client coordinates, so the sentinel is how a test tells "dropped where the pointer was" apart from
// "added at the palette's default slot".
const flow = vi.hoisted(() => ({ props: undefined as Record<string, unknown> | undefined, dropPosition: { x: 11, y: 22 } }));

vi.mock("@xyflow/react", async (importOriginal) => ({
	...(await importOriginal<typeof import("@xyflow/react")>()),
	Background: () => null,
	Controls: () => null,
	Handle: () => null,
	ReactFlowProvider: ({ children }: { readonly children: React.ReactNode }) => children,
	useReactFlow: () => ({ screenToFlowPosition: () => flow.dropPosition }),
	ReactFlow: (props: Record<string, unknown>) => {
		flow.props = props;
		// A button, not a div: the drop handlers the canvas passes down would otherwise trip the a11y rules on a static element.
		return (
			<button
				type="button"
				data-testid="react-flow"
				onDrop={props["onDrop"] as never}
				onDragOver={props["onDragOver"] as never}
			/>
		);
	},
}));

function flowProps(): ReactFlowProps {
	if (flow.props === undefined) {
		throw new Error("the canvas never rendered a ReactFlow");
	}
	return flow.props as ReactFlowProps;
}

function flowNodes(): readonly GraphWorkflowCanvasNode[] {
	return (flowProps().nodes ?? []) as GraphWorkflowCanvasNode[];
}

function flowEdges(): readonly GraphWorkflowCanvasEdge[] {
	return (flowProps().edges ?? []) as GraphWorkflowCanvasEdge[];
}

interface HarnessProps {
	readonly initial?: GraphWorkflowGraph;
	readonly issues?: readonly GraphWorkflowGraphIssue[];
	readonly onSelectNode?: (nodeKey: string | undefined) => void;
	readonly onSelectEdge?: (edgeId: string | undefined) => void;
}

/**
 * The canvas is controlled, so a test needs the same wiring the page will have: the editor hook above it, and the
 * selection below it. The issue list is echoed into the DOM so a test can assert on what the editor derived without
 * reaching into the hook.
 */
function Harness({ initial, issues, onSelectNode, onSelectEdge }: HarnessProps) {
	const editor = useGraphWorkflowEditor(initial);
	const [selectedNodeKey, setSelectedNodeKey] = useState<string | undefined>(undefined);
	const [selectedEdgeId, setSelectedEdgeId] = useState<string | undefined>(undefined);
	const effective = issues ?? editor.issues;

	return (
		<>
			<div data-testid="harness-issues">{effective.map((issue) => `${issue.rule}:${issue.subject ?? ""}`).join(",")}</div>
			<div data-testid="harness-selection">{`${selectedNodeKey ?? ""}|${selectedEdgeId ?? ""}`}</div>
			<GraphWorkflowEditorCanvas
				editor={editor}
				issues={effective}
				selectedNodeKey={selectedNodeKey}
				selectedEdgeId={selectedEdgeId}
				onSelectNode={(key) => {
					setSelectedNodeKey(key);
					onSelectNode?.(key);
				}}
				onSelectEdge={(id) => {
					setSelectedEdgeId(id);
					onSelectEdge?.(id);
				}}
				toolbar={<button type="button" data-testid="harness-toolbar" />}
			/>
		</>
	);
}

function issueText(): string {
	return screen.getByTestId("harness-issues").textContent ?? "";
}

/** jsdom has no DataTransfer — the minimum the drop handler reads. */
function makeDataTransfer(): DataTransfer {
	const store: Record<string, string> = {};
	return {
		setData: (key: string, value: string) => {
			store[key] = value;
		},
		getData: (key: string) => store[key] ?? "",
		dropEffect: "",
		effectAllowed: "",
	} as unknown as DataTransfer;
}

afterEach(() => {
	cleanup();
	flow.props = undefined;
});

describe("GraphWorkflowEditorCanvas palette", () => {
	it("adds a node on a palette click, with the minted key", () => {
		renderWithProviders(<Harness />);

		fireEvent.click(screen.getByTestId("graph-workflow-palette-agent"));

		expect(flowNodes().map((node) => node.id)).toEqual(["agent-1"]);
		expect(flowNodes()[0]?.type).toBe("agent");
		// A card is registered for every one of the eight type names the mapper writes, or a kind renders as nothing.
		expect(Object.keys(flowProps().nodeTypes ?? {}).toSorted()).toEqual(Object.values(graphWorkflowNodeTypeByKind).toSorted());
	});

	it("drops a dragged palette entry onto the pane at the drop position", () => {
		renderWithProviders(<Harness />);

		const transfer = makeDataTransfer();
		fireEvent.dragStart(screen.getByTestId("graph-workflow-palette-drag-tool"), { dataTransfer: transfer });
		fireEvent.dragOver(screen.getByTestId("react-flow"), { dataTransfer: transfer });
		fireEvent.drop(screen.getByTestId("react-flow"), { dataTransfer: transfer });

		expect(flowNodes().map((node) => node.id)).toEqual(["tool-1"]);
		// The flow projection of the pointer, not the palette-click fallback slot.
		expect(flowNodes()[0]?.position).toEqual(flow.dropPosition);
	});

	it("ignores a foreign drop rather than throwing out of the handler", () => {
		renderWithProviders(<Harness />);

		const transfer = makeDataTransfer();
		transfer.setData("text/plain", "not a node");
		fireEvent.drop(screen.getByTestId("react-flow"), { dataTransfer: transfer });

		expect(flowNodes()).toHaveLength(0);
	});

	it("refuses a node past the cap with a notice the operator can dismiss", () => {
		const full: GraphWorkflowGraph = {
			schemaVersion: 1,
			nodes: Array.from({ length: GRAPH_WORKFLOW_MAX_NODES }, (_unused, index) => ({
				key: `n${index}`,
				kind: "Agent",
				position: { x: 0, y: index },
				config: {},
			})),
			edges: [],
		};
		renderWithProviders(<Harness initial={full} />);

		// The palette is disabled at the cap, so the refusal is reached the way a drop reaches it.
		const transfer = makeDataTransfer();
		transfer.setData("application/xe-graph-workflow", JSON.stringify({ kind: "Agent" }));
		fireEvent.drop(screen.getByTestId("react-flow"), { dataTransfer: transfer });

		expect(screen.getByTestId("graph-workflow-editor-refusal").textContent).toContain("as many nodes as a run accepts");
		expect(flowNodes()).toHaveLength(GRAPH_WORKFLOW_MAX_NODES);
		expect((screen.getByTestId("graph-workflow-palette-agent") as HTMLButtonElement).disabled).toBe(true);

		fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));
		expect(screen.queryByTestId("graph-workflow-editor-refusal")).toBeNull();
	});
});

describe("GraphWorkflowEditorCanvas connecting", () => {
	it("writes the handle into sourceHandle, label and a pathless condition for a Condition branch", () => {
		const graph: GraphWorkflowGraph = {
			...eightNodeGraph,
			edges: (eightNodeGraph.edges ?? []).filter((edge) => edge.key !== "e4"),
		};
		renderWithProviders(<Harness initial={graph} />);

		act(() => {
			flowProps().onConnect?.({ source: "check", target: "lookup", sourceHandle: "false", targetHandle: null });
		});

		const edge = flowEdges().find((candidate) => candidate.source === "check" && candidate.target === "lookup");
		expect(edge?.sourceHandle).toBe("false");
		expect(edge?.label).toBe("false");
		expect(edge?.data?.condition).toEqual({ op: "Eq", value: "false" });
	});

	it("prefills a Pause decision branch in full and clears pauseDecisionUnroutable", () => {
		const graph: GraphWorkflowGraph = {
			...eightNodeGraph,
			edges: (eightNodeGraph.edges ?? []).filter((edge) => edge.key !== "e5"),
		};
		renderWithProviders(<Harness initial={graph} />);

		expect(issueText()).toContain("pauseDecisionUnroutable:review");

		act(() => {
			flowProps().onConnect?.({ source: "review", target: "fanout", sourceHandle: "Approve", targetHandle: null });
		});

		const edge = flowEdges().find((candidate) => candidate.source === "review" && candidate.target === "fanout");
		expect(edge?.data?.condition).toEqual({ path: "output.decision", op: "Eq", value: "Approve" });
		expect(issueText()).not.toContain("pauseDecisionUnroutable:review");
	});

	it("refuses a second unconditional edge over one pair and accepts a second conditional one", () => {
		renderWithProviders(<Harness initial={eightNodeGraph} />);

		act(() => {
			flowProps().onConnect?.({ source: "lookup", target: "fanout", sourceHandle: null, targetHandle: null });
		});

		expect(flowEdges().filter((edge) => edge.source === "lookup" && edge.target === "fanout")).toHaveLength(1);
		expect(screen.getByTestId("graph-workflow-editor-refusal").textContent).toContain("without a condition");

		act(() => {
			flowProps().onConnect?.({ source: "review", target: "done", sourceHandle: "Approve", targetHandle: null });
		});

		expect(flowEdges().filter((edge) => edge.source === "review" && edge.target === "done")).toHaveLength(2);
	});
});

describe("GraphWorkflowEditorCanvas selection and issues", () => {
	it("selects a node, selects an edge, and clears both on a pane click", () => {
		const onSelectNode = vi.fn();
		const onSelectEdge = vi.fn();
		renderWithProviders(<Harness initial={eightNodeGraph} onSelectNode={onSelectNode} onSelectEdge={onSelectEdge} />);

		const event = new MouseEvent("click") as unknown as React.MouseEvent;
		act(() => {
			flowProps().onNodeClick?.(event, { id: "analyze" } as Node);
		});
		expect(onSelectNode).toHaveBeenLastCalledWith("analyze");
		expect(screen.getByTestId("harness-selection").textContent).toBe("analyze|");
		expect(flowNodes().find((node) => node.id === "analyze")?.selected).toBe(true);

		act(() => {
			flowProps().onEdgeClick?.(event, { id: "e3", source: "check", target: "review" } as Edge);
		});
		expect(onSelectEdge).toHaveBeenLastCalledWith("e3");
		expect(screen.getByTestId("harness-selection").textContent).toBe("|e3");
		expect(flowEdges().find((edge) => edge.id === "e3")?.selected).toBe(true);

		act(() => {
			flowProps().onPaneClick?.(event);
		});
		expect(onSelectNode).toHaveBeenLastCalledWith(undefined);
		expect(onSelectEdge).toHaveBeenLastCalledWith(undefined);
		expect(screen.getByTestId("harness-selection").textContent).toBe("|");
	});

	it("rings the node and the edge a keyed issue names, and nothing else", () => {
		renderWithProviders(
			<Harness
				initial={eightNodeGraph}
				issues={[
					{ rule: "invalidJson", subject: "analyze" },
					{ rule: "serverRejected", subject: "e3", message: "the server said no" },
					{ rule: "noStart" },
				]}
			/>,
		);

		expect(flowNodes().find((node) => node.id === "analyze")?.data["hasIssue"]).toBe(true);
		expect(flowNodes().find((node) => node.id === "check")?.data["hasIssue"]).toBe(false);
		expect(flowEdges().find((edge) => edge.id === "e3")?.className).toBeTruthy();
		expect(flowEdges().find((edge) => edge.id === "e1")?.className).toBeUndefined();
	});

	it("renders the toolbar slot and auto-arranges on request", () => {
		renderWithProviders(<Harness initial={eightNodeGraph} />);

		expect(screen.getByTestId("harness-toolbar")).toBeTruthy();
		// The fixture stacks the graph in one column; the layout is what spreads it across ranks.
		expect(flowNodes().find((node) => node.id === "analyze")?.position).toEqual({ x: 0, y: 120 });

		fireEvent.click(screen.getByTestId("graph-workflow-auto-arrange"));

		// `analyze` is rank 1 in the layout, so auto-arrange moves it off the column the fixture saved it in.
		expect(flowNodes().find((node) => node.id === "analyze")?.position).toEqual({ x: 280, y: 0 });
		expect(flowNodes()).toHaveLength(8);
	});
});
