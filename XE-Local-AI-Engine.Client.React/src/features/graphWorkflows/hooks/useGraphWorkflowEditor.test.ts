// @vitest-environment jsdom

// The editor's rules, tested on the hook rather than through the canvas: jsdom gives React Flow a 0×0 viewport, so a
// connect gesture can never be made with a pointer here. What `onConnect` DOES with a connection is the behaviour that
// matters, and it is fully reachable by calling it.

import { act, renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { useGraphWorkflowEditor } from "@/features/graphWorkflows/hooks/useGraphWorkflowEditor";
import { GRAPH_WORKFLOW_MAX_NODES, type GraphWorkflowGraph } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { eightNodeGraph } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";

/** The fixture graph minus the named edges, so a test can author the gap the rule under test is about. */
function withoutEdges(...keys: readonly string[]): GraphWorkflowGraph {
	return { ...eightNodeGraph, edges: (eightNodeGraph.edges ?? []).filter((edge) => !keys.includes(edge.key ?? "")) };
}

function connection(source: string, target: string, sourceHandle?: string) {
	return { source, target, sourceHandle: sourceHandle ?? null, targetHandle: null };
}

describe("useGraphWorkflowEditor palette", () => {
	it("mints kind-slugged keys in sequence and returns the new one", () => {
		const { result } = renderHook(() => useGraphWorkflowEditor(undefined));

		let first: string | undefined;
		let second: string | undefined;
		act(() => {
			first = result.current.addNode("Agent");
		});
		act(() => {
			second = result.current.addNode("Agent");
		});

		expect(first).toBe("agent-1");
		expect(second).toBe("agent-2");
		expect(result.current.nodes.map((node) => node.id)).toEqual(["agent-1", "agent-2"]);
		expect(result.current.nodes[0]?.type).toBe("agent");
		expect(result.current.nodes[0]?.data.kind).toBe("Agent");
		// F-1: an Agent node starts on three attempts, a structural kind on one.
		expect(result.current.nodes[0]?.data.maxAttempts).toBe(3);
	});

	it("refuses a node past the cap, returns no key and reports the refusal", () => {
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
		const { result } = renderHook(() => useGraphWorkflowEditor(full));

		expect(result.current.canAddNode).toBe(false);

		let key: string | undefined = "not-called";
		act(() => {
			key = result.current.addNode("Agent");
		});

		expect(key).toBeUndefined();
		expect(result.current.nodes).toHaveLength(GRAPH_WORKFLOW_MAX_NODES);
		expect(result.current.lastRefusal?.rule).toBe("tooManyNodes");

		act(() => {
			result.current.dismissRefusal();
		});
		expect(result.current.lastRefusal).toBeUndefined();

		// The same refusal a second time has to be visible again, which is what the sequence number is for.
		act(() => {
			result.current.addNode("Agent");
		});
		expect(result.current.lastRefusal?.rule).toBe("tooManyNodes");
	});
});

describe("useGraphWorkflowEditor onConnect", () => {
	it("prefills a Condition false branch with no path, so the edge inherits the node's", () => {
		const { result } = renderHook(() => useGraphWorkflowEditor(withoutEdges("e4")));

		act(() => {
			result.current.onConnect(connection("check", "lookup", "false"));
		});

		const edge = result.current.edges.find((candidate) => candidate.source === "check" && candidate.target === "lookup");
		expect(edge?.sourceHandle).toBe("false");
		expect(edge?.label).toBe("false");
		expect(edge?.data?.label).toBe("false");
		expect(edge?.data?.condition).toEqual({ op: "Eq", value: "false" });
		expect(edge?.data?.condition?.path).toBeUndefined();
	});

	it("prefills a Pause decision branch in full and clears pauseDecisionUnroutable", () => {
		const { result } = renderHook(() => useGraphWorkflowEditor(withoutEdges("e5")));

		expect(result.current.issues).toContainEqual({ rule: "pauseDecisionUnroutable", subject: "review" });

		act(() => {
			result.current.onConnect(connection("review", "fanout", "Approve"));
		});

		const edge = result.current.edges.find((candidate) => candidate.source === "review" && candidate.target === "fanout");
		expect(edge?.sourceHandle).toBe("Approve");
		expect(edge?.label).toBe("Approve");
		expect(edge?.data?.condition).toEqual({ path: "output.decision", op: "Eq", value: "Approve" });
		expect(result.current.issues).not.toContainEqual({ rule: "pauseDecisionUnroutable", subject: "review" });
	});

	it("refuses a second unconditional edge over one pair but accepts a second conditional one", () => {
		const { result } = renderHook(() => useGraphWorkflowEditor(eightNodeGraph));

		// `e6` already joins lookup → fanout unconditionally.
		act(() => {
			result.current.onConnect(connection("lookup", "fanout"));
		});

		expect(result.current.edges.filter((edge) => edge.source === "lookup" && edge.target === "fanout")).toHaveLength(1);
		expect(result.current.lastRefusal?.rule).toBe("parallelEdgesBothUnconditional");

		// `review` → `done` is conditional (`e9`), so a second conditional edge over that pair is the legal shape.
		act(() => {
			result.current.onConnect(connection("review", "done", "Approve"));
		});

		expect(result.current.edges.filter((edge) => edge.source === "review" && edge.target === "done")).toHaveLength(2);
	});

	it("mints edge keys over the one namespace it shares with the nodes", () => {
		const { result } = renderHook(() => useGraphWorkflowEditor(undefined));

		act(() => {
			result.current.addNode("Start");
		});
		act(() => {
			result.current.addNode("End");
		});
		act(() => {
			result.current.onConnect(connection("start-1", "end-1"));
		});

		expect(result.current.edges[0]?.id).toBe("e1");
		expect(result.current.edges[0]?.data?.condition).toBeUndefined();
	});
});

describe("useGraphWorkflowEditor mutations", () => {
	it("patches node data without letting the kind or the key move", () => {
		const { result } = renderHook(() => useGraphWorkflowEditor(eightNodeGraph));

		act(() => {
			result.current.updateNodeData("analyze", { label: "Renamed", kind: "End", key: "elsewhere" });
		});

		const node = result.current.nodes.find((candidate) => candidate.id === "analyze");
		expect(node?.data.label).toBe("Renamed");
		expect(node?.data.kind).toBe("Agent");
		expect(node?.data.key).toBe("analyze");
	});

	it("cascades a rename into the edges and refuses a collision or a bad charset", () => {
		const { result } = renderHook(() => useGraphWorkflowEditor(eightNodeGraph));

		expect(result.current.renameNode("analyze", "check")).toBe("collision");
		expect(result.current.renameNode("analyze", "not a key")).toBe("invalid");

		act(() => {
			expect(result.current.renameNode("analyze", "analyse")).toBe("ok");
		});

		expect(result.current.nodes.some((node) => node.id === "analyse")).toBe(true);
		expect(result.current.edges.find((edge) => edge.id === "e1")?.target).toBe("analyse");
		expect(result.current.edges.find((edge) => edge.id === "e2")?.source).toBe("analyse");
	});

	it("clears an edge condition when the panel patches it away, and keeps the label in step", () => {
		const { result } = renderHook(() => useGraphWorkflowEditor(eightNodeGraph));

		act(() => {
			result.current.updateEdgeData("e3", { condition: undefined, label: "" });
		});

		const edge = result.current.edges.find((candidate) => candidate.id === "e3");
		expect(edge?.data?.condition).toBeUndefined();
		expect(edge?.label).toBeUndefined();
	});

	it("removes a node together with every edge that touched it", () => {
		const { result } = renderHook(() => useGraphWorkflowEditor(eightNodeGraph));

		act(() => {
			result.current.removeNode("review");
		});

		expect(result.current.nodes.some((node) => node.id === "review")).toBe(false);
		expect(result.current.edges.some((edge) => edge.source === "review" || edge.target === "review")).toBe(false);
		expect(result.current.issues.some((issue) => issue.rule === "unknownEdgeEndpoint")).toBe(false);
	});

	it("removes a single edge", () => {
		const { result } = renderHook(() => useGraphWorkflowEditor(eightNodeGraph));

		act(() => {
			result.current.removeEdge("e9");
		});

		expect(result.current.edges.some((edge) => edge.id === "e9")).toBe(false);
	});

	it("auto-arranges every node onto the layout's positions", () => {
		const { result } = renderHook(() => useGraphWorkflowEditor(eightNodeGraph));

		act(() => {
			result.current.autoArrange();
		});

		// Start is rank 0, and the layout is top-aligned, so it lands at the origin whatever it was saved at.
		expect(result.current.nodes.find((node) => node.id === "start")?.position).toEqual({ x: 0, y: 0 });
		// Every node keeps its data — an auto-arrange is a move, not a reload.
		expect(result.current.nodes).toHaveLength(8);
		expect(result.current.nodes.find((node) => node.id === "analyze")?.data.label).toBe("Analyze");
	});
});

describe("useGraphWorkflowEditor dirty state", () => {
	it("opens a saved graph clean, goes dirty on an edit, and is clean again once saved", () => {
		const { result } = renderHook(() => useGraphWorkflowEditor(eightNodeGraph));

		expect(result.current.isDirty).toBe(false);

		act(() => {
			result.current.updateNodeData("analyze", { label: "Analyse" });
		});
		expect(result.current.isDirty).toBe(true);

		act(() => {
			result.current.markSaved(result.current.graph);
		});
		expect(result.current.isDirty).toBe(false);
		// The canvas is untouched by a save: the operator keeps their viewport and their selection.
		expect(result.current.nodes.find((node) => node.id === "analyze")?.data.label).toBe("Analyse");
	});

	it("reset loads another graph and moves the baseline with it", () => {
		const { result } = renderHook(() => useGraphWorkflowEditor(undefined));

		act(() => {
			result.current.addNode("Agent");
		});
		expect(result.current.isDirty).toBe(true);

		act(() => {
			result.current.reset(eightNodeGraph);
		});

		expect(result.current.isDirty).toBe(false);
		expect(result.current.nodes).toHaveLength(8);
		expect(result.current.lastRefusal).toBeUndefined();
	});
});
