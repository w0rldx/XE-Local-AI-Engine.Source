// The wire ↔ canvas mapping, which is where a definition quietly loses an edge, a branch or an operator's formatting.
// The anchor case is the round trip over the brief's own eight-node graph — the body the S2 live round actually sent —
// because a mapping that only agrees with itself proves nothing about what the server stores.

import { describe, expect, it } from "vitest";

import {
	canvasToGraph,
	defaultNodeData,
	type GraphWorkflowCanvas,
	type GraphWorkflowCanvasEdge,
	type GraphWorkflowCanvasNode,
	type GraphWorkflowCanvasNodeData,
	graphToCanvas,
	graphWorkflowNodeTypeByKind,
	graphWorkflowsEqual,
	mintEdgeKey,
	mintNodeKey,
	renameNodeKey,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { NODE_SPACING_Y, RANK_SPACING_X } from "@/features/graphWorkflows/models/GraphWorkflowLayout";
import type {
	GraphWorkflowGraph,
	GraphWorkflowGraphEdge,
	GraphWorkflowGraphNode,
	GraphWorkflowNodeKind,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { eightNodeGraph } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";

function clone(): GraphWorkflowGraph {
	return JSON.parse(JSON.stringify(eightNodeGraph)) as GraphWorkflowGraph;
}

function graph(nodes: GraphWorkflowGraphNode[], edges: GraphWorkflowGraphEdge[]): GraphWorkflowGraph {
	return { schemaVersion: 1, nodes, edges };
}

function canvasNode(data: GraphWorkflowCanvasNodeData, x = 0, y = 0): GraphWorkflowCanvasNode {
	return { id: data.key, type: graphWorkflowNodeTypeByKind[data.kind], position: { x, y }, data };
}

/** Narrows a canvas node to one kind. Throws rather than asserting: an assertion outside `it()` is a lint error, and a
 * lookup that finds nothing has to fail loudly instead of handing the test an `undefined` to compare against. */
function dataOfKind<K extends GraphWorkflowNodeKind>(
	canvas: GraphWorkflowCanvas,
	key: string,
	kind: K,
): Extract<GraphWorkflowCanvasNodeData, { kind: K }> {
	const data = canvas.nodes.find((node) => node.id === key)?.data;
	if (data?.kind !== kind) {
		throw new Error(`Expected canvas node "${key}" to be a ${kind} node, found ${String(data?.kind)}.`);
	}
	return data as Extract<GraphWorkflowCanvasNodeData, { kind: K }>;
}

function edgeOf(canvas: GraphWorkflowCanvas, key: string): GraphWorkflowCanvasEdge {
	const edge = canvas.edges.find((candidate) => candidate.id === key);
	if (!edge) {
		throw new Error(`No canvas edge "${key}".`);
	}
	return edge;
}

function wireEdge(result: GraphWorkflowGraph, key: string): GraphWorkflowGraphEdge {
	const edge = (result.edges ?? []).find((candidate) => candidate.key === key);
	if (!edge) {
		throw new Error(`No wire edge "${key}".`);
	}
	return edge;
}

describe("graphToCanvas / canvasToGraph round trip", () => {
	it("round-trips the brief's eight-node graph with no issues and no drift", () => {
		const canvas = graphToCanvas(eightNodeGraph);
		const { graph: result, issues } = canvasToGraph(canvas.nodes, canvas.edges);

		expect(issues).toEqual([]);
		expect(graphWorkflowsEqual(result, eightNodeGraph)).toBe(true);
		expect(result.nodes).toHaveLength(8);
		expect(result.edges).toHaveLength(9);
	});

	it("keeps the node key as the React Flow id and the edge key as the edge id", () => {
		const canvas = graphToCanvas(eightNodeGraph);

		expect(canvas.nodes.map((node) => node.id)).toEqual([
			"start",
			"analyze",
			"check",
			"review",
			"lookup",
			"fanout",
			"merge",
			"done",
		]);
		expect(canvas.edges.map((edge) => edge.id)).toEqual(["e1", "e2", "e3", "e4", "e5", "e6", "e7", "e8", "e9"]);
		expect(canvas.nodes.map((node) => node.type)).toEqual([
			"start",
			"agent",
			"condition",
			"pause",
			"tool",
			"parallel",
			"join",
			"end",
		]);
	});

	it("keeps a stored position verbatim and restores sourceHandle and label", () => {
		const canvas = graphToCanvas(eightNodeGraph);

		expect(canvas.nodes.find((node) => node.id === "review")?.position).toEqual({ x: -120, y: 360 });
		expect(edgeOf(canvas, "e3").sourceHandle).toBe("true");
		expect(edgeOf(canvas, "e5").sourceHandle).toBe("Approve");
		expect(edgeOf(canvas, "e9").sourceHandle).toBe("Reject");
		expect(edgeOf(canvas, "e5").data?.label).toBe("approved");
		expect(edgeOf(canvas, "e5").label).toBe("approved");
		expect(edgeOf(canvas, "e1").sourceHandle).toBeUndefined();
	});

	it("lays out a node that arrives without a position rather than dropping it", () => {
		const source = clone();
		const analyze = (source.nodes ?? []).find((node) => node.key === "analyze");
		delete analyze?.position;

		const canvas = graphToCanvas(source);

		expect(canvas.nodes).toHaveLength(8);
		// Rank 1 of the layered layout: `start` → `analyze` is one hop, and nothing else shares that rank.
		expect(canvas.nodes.find((node) => node.id === "analyze")?.position).toEqual({ x: RANK_SPACING_X, y: 0 });
		// Every other node kept the position the wire carried.
		expect(canvas.nodes.find((node) => node.id === "check")?.position).toEqual({ x: 0, y: 240 });
	});

	it("recomputes every position when asked to relayout, and that is a real edit", () => {
		const canvas = graphToCanvas(eightNodeGraph, { relayout: true });

		expect(canvas.nodes.find((node) => node.id === "start")?.position).toEqual({ x: 0, y: 0 });
		expect(canvas.nodes.find((node) => node.id === "analyze")?.position).toEqual({ x: RANK_SPACING_X, y: 0 });
		const { graph: result } = canvasToGraph(canvas.nodes, canvas.edges);
		expect(graphWorkflowsEqual(result, eightNodeGraph)).toBe(false);
	});

	it("rounds positions to integers so a sub-pixel drag is not a permanent edit", () => {
		const canvas = graphToCanvas(eightNodeGraph);
		const dragged = canvas.nodes.map((node) => (node.id === "start" ? { ...node, position: { x: 0.4, y: -0.4 } } : node));

		const { graph: result } = canvasToGraph(dragged, canvas.edges);

		expect((result.nodes ?? []).find((node) => node.key === "start")?.position).toEqual({ x: 0, y: -0 });
	});
});

describe("node config conversion", () => {
	it("holds the three JSON-shaped fields as pretty-printed text and parses them back to objects", () => {
		const canvas = graphToCanvas(eightNodeGraph);
		const agent = dataOfKind(canvas, "analyze", "Agent");

		expect(agent.responseJsonSchema).toContain('"requiresReview"');
		expect(agent.responseJsonSchema).toContain("\n");
		expect(agent.includeUpstreamOutputs).toBe(true);
		expect(agent.maxAttempts).toBe(3);

		const { graph: result } = canvasToGraph(canvas.nodes, canvas.edges);
		const config = (result.nodes ?? []).find((node) => node.key === "analyze")?.config as Record<string, unknown>;
		expect(config["responseJsonSchema"]).toEqual({ type: "object", properties: { requiresReview: { type: "boolean" } } });
	});

	it("converts the Tool node's arguments object and binding map both ways", () => {
		const canvas = graphToCanvas(eightNodeGraph);
		const tool = dataOfKind(canvas, "lookup", "Tool");

		expect(JSON.parse(tool.argumentsJson)).toEqual({ path: "notes.md" });
		expect(tool.argumentBindings).toEqual([{ parameter: "path", path: "output.json.path" }]);

		const { graph: result } = canvasToGraph(canvas.nodes, canvas.edges);
		const config = (result.nodes ?? []).find((node) => node.key === "lookup")?.config as Record<string, unknown>;
		expect(config["arguments"]).toEqual({ path: "notes.md" });
		expect(config["argumentBindings"]).toEqual({ path: "output.json.path" });
	});

	it("omits argumentBindings entirely for an empty map, drops an empty parameter and takes the last duplicate", () => {
		const empty = canvasToGraph([canvasNode(defaultNodeData("Tool", "tool-1"))], []);
		const emptyConfig = (empty.graph.nodes ?? [])[0]?.config as Record<string, unknown>;
		expect(Object.keys(emptyConfig)).not.toContain("argumentBindings");
		expect(emptyConfig["arguments"]).toEqual({});

		const filled = canvasToGraph(
			[
				canvasNode({
					...defaultNodeData("Tool", "tool-1"),
					kind: "Tool",
					toolName: "read_file",
					argumentsJson: "",
					argumentBindings: [
						{ parameter: "path", path: "first" },
						{ parameter: "", path: "dropped" },
						{ parameter: "path", path: "second" },
					],
				}),
			],
			[],
		);
		const config = (filled.graph.nodes ?? [])[0]?.config as Record<string, unknown>;
		expect(config["argumentBindings"]).toEqual({ path: "second" });
	});

	it("reads a malformed or missing config as the kind's defaults instead of throwing", () => {
		const canvas = graphToCanvas(
			graph(
				[
					{ key: "start", kind: "Start", config: "not an object" },
					{ key: "agent-1", kind: "Agent", config: { includeUpstreamOutputs: "yes" } },
					{ key: "pause-1", kind: "Pause", config: { allowedDecisions: ["Approve", "Shrug", 7] } },
					{ key: "done", kind: "End", config: null },
				],
				[],
			),
		);

		expect(dataOfKind(canvas, "start", "Start").defaultInput).toBeNull();
		expect(dataOfKind(canvas, "agent-1", "Agent").includeUpstreamOutputs).toBe(true);
		// An unrenderable decision is dropped rather than mislabelled — it drives a button.
		expect(dataOfKind(canvas, "pause-1", "Pause").allowedDecisions).toEqual(["Approve"]);
		expect(dataOfKind(canvas, "done", "End").outcome).toBe("");
	});

	it("keeps invalid JSON text on the canvas and surfaces it as one invalidJson issue per bad field", () => {
		const node = canvasNode({
			...defaultNodeData("Agent", "agent-1"),
			kind: "Agent",
			agentDefinitionId: null,
			instructions: "Do the thing.",
			model: null,
			reasoningEffort: null,
			responseJsonSchema: '{ "type": "object"',
			includeUpstreamOutputs: true,
		});

		const { graph: result, issues } = canvasToGraph([node], []);

		// The half-typed text is still on the canvas — the editor did not rewrite what the operator was typing.
		expect(dataOfKind({ nodes: [node], edges: [] }, "agent-1", "Agent").responseJsonSchema).toBe('{ "type": "object"');
		expect(issues).toEqual([{ rule: "invalidJson", subject: "agent-1" }]);
		const config = (result.nodes ?? [])[0]?.config as Record<string, unknown>;
		expect(config["responseJsonSchema"]).toBeNull();
	});

	it("reports JSON that parses but is not an object where the wire needs an object", () => {
		const start = canvasNode({
			...defaultNodeData("Start", "start"),
			kind: "Start",
			inputSchema: "[1, 2]",
			defaultInput: '"a plain string is legal here"',
		});

		const { graph: result, issues } = canvasToGraph([start], []);

		expect(issues.map((issue) => issue.rule)).toEqual(["invalidJson"]);
		const config = (result.nodes ?? [])[0]?.config as Record<string, unknown>;
		expect(config["inputSchema"]).toBeNull();
		// `defaultInput` is `unknown` on the wire, so any JSON value survives.
		expect(config["defaultInput"]).toBe("a plain string is legal here");
	});

	it("omits an empty label and a default joinPolicy, and writes Any", () => {
		const both = canvasToGraph(
			[
				canvasNode({ ...defaultNodeData("Join", "merge"), kind: "Join", joinPolicy: "Any" }),
				canvasNode({ ...defaultNodeData("Join", "merge-2"), kind: "Join", label: "Merge" }),
			],
			[],
		);

		expect((both.graph.nodes ?? [])[0]).toMatchObject({ joinPolicy: "Any" });
		expect(Object.keys((both.graph.nodes ?? [])[0] ?? {})).not.toContain("label");
		expect(Object.keys((both.graph.nodes ?? [])[1] ?? {})).not.toContain("joinPolicy");
		expect((both.graph.nodes ?? [])[1]?.label).toBe("Merge");
	});
});

describe("edge conditions", () => {
	it("keeps a string value as itself and a non-string value as its JSON text", () => {
		const canvas = graphToCanvas(eightNodeGraph);

		expect(edgeOf(canvas, "e5").data?.condition).toEqual({ path: "output.decision", op: "Eq", value: "Approve" });
		expect(edgeOf(canvas, "e3").data?.condition).toEqual({ op: "Eq", value: "true" });
	});

	it("normalises a stored lowercase operator to its canonical member and writes the canonical one back", () => {
		const source = graph(
			[
				{ key: "check", kind: "Condition", position: { x: 0, y: 0 }, config: { path: "output.json.ok" } },
				{ key: "done", kind: "End", position: { x: 0, y: 120 }, config: { outcome: "completed" } },
			],
			[{ key: "e1", from: "check", to: "done", condition: { op: "eq", value: true } }],
		);

		const canvas = graphToCanvas(source);
		expect(edgeOf(canvas, "e1").data?.condition?.op).toBe("Eq");

		const { graph: result } = canvasToGraph(canvas.nodes, canvas.edges);
		expect(wireEdge(result, "e1").condition).toEqual({ op: "Eq", value: true });
		// The two spellings are the same branch: the dirty check normalises the token as the server's parser does, so
		// opening a graph stored with `eq` does not report an edit that was never made.
		const canonical = graph(source.nodes ?? [], [{ ...(source.edges ?? [])[0], condition: { op: "Eq", value: true } }]);
		expect(graphWorkflowsEqual(canonical, source)).toBe(true);
		// The handle IS re-derived, though, and persisting it is a real (and intended) edit.
		expect(wireEdge(result, "e1").sourceHandle).toBe("true");
	});

	it("drops a condition whose operator it cannot read rather than guessing one", () => {
		const source = graph(
			[
				{ key: "check", kind: "Condition", config: { path: "a" } },
				{ key: "done", kind: "End", config: {} },
			],
			[{ key: "e1", from: "check", to: "done", condition: { op: "approximately", value: 1 } }],
		);

		const canvas = graphToCanvas(source);

		expect(edgeOf(canvas, "e1").data?.condition).toBeUndefined();
		// The edge survives, unconditional; `validateGraphWorkflowGraph` is what tells the operator, over the wire graph.
		expect(canvas.edges).toHaveLength(1);
	});

	it("round-trips Exists and NotExists without a value", () => {
		const source = graph(
			[
				{ key: "check", kind: "Condition", position: { x: 0, y: 0 }, config: { path: "a" } },
				{ key: "one", kind: "End", position: { x: 0, y: 120 }, config: { outcome: "completed", resultPath: null } },
				{ key: "two", kind: "End", position: { x: 120, y: 120 }, config: { outcome: "completed", resultPath: null } },
			],
			[
				{ key: "e1", from: "check", to: "one", condition: { path: "output.json.id", op: "Exists" } },
				{ key: "e2", from: "check", to: "two", condition: { path: "output.json.id", op: "NotExists" } },
			],
		);

		const canvas = graphToCanvas(source);
		expect(edgeOf(canvas, "e1").data?.condition).toEqual({ path: "output.json.id", op: "Exists", value: "" });

		const { graph: result } = canvasToGraph(canvas.nodes, canvas.edges);
		expect(Object.keys(wireEdge(result, "e1").condition ?? {})).toEqual(["path", "op"]);
		expect(Object.keys(wireEdge(result, "e2").condition ?? {})).toEqual(["path", "op"]);
		expect(graphWorkflowsEqual(result, source)).toBe(true);
	});

	it("falls back to the raw string when a condition value is not JSON", () => {
		const canvas = graphToCanvas(eightNodeGraph);
		const { graph: result } = canvasToGraph(canvas.nodes, canvas.edges);

		expect(wireEdge(result, "e5").condition?.value).toBe("Approve");
		expect(wireEdge(result, "e3").condition?.value).toBe(true);
	});
});

describe("source handle re-derivation", () => {
	const nodes: GraphWorkflowGraphNode[] = [
		{ key: "check", kind: "Condition", config: { path: "a" } },
		{ key: "review", kind: "Pause", config: { prompt: "?", allowedDecisions: ["Approve", "Reject"] } },
		{ key: "plain", kind: "Agent", config: { instructions: "x" } },
		{ key: "done", kind: "End", config: {} },
	];

	it("takes a stored sourceHandle verbatim, whatever the label says", () => {
		const canvas = graphToCanvas(graph(nodes, [{ key: "e1", from: "check", to: "done", sourceHandle: "false", label: "yes" }]));

		expect(edgeOf(canvas, "e1").sourceHandle).toBe("false");
	});

	it("re-derives a Condition handle from the label, then from an Eq boolean condition", () => {
		const canvas = graphToCanvas(
			graph(nodes, [
				{ key: "e1", from: "check", to: "done", label: "true" },
				{ key: "e2", from: "check", to: "done", condition: { op: "Eq", value: false } },
				{ key: "e3", from: "check", to: "done", condition: { op: "Ne", value: true } },
			]),
		);

		expect(edgeOf(canvas, "e1").sourceHandle).toBe("true");
		expect(edgeOf(canvas, "e2").sourceHandle).toBe("false");
		// `Ne` is not a handle: the canvas falls back to the default handle rather than inventing a branch.
		expect(edgeOf(canvas, "e3").sourceHandle).toBeUndefined();
	});

	it("re-derives a Pause handle from the label, then from the decision the condition names", () => {
		const canvas = graphToCanvas(
			graph(nodes, [
				{ key: "e1", from: "review", to: "done", label: "Reject" },
				{ key: "e2", from: "review", to: "done", condition: { path: "output.decision", op: "Eq", value: "Approve" } },
				{ key: "e3", from: "review", to: "done", label: "approved" },
			]),
		);

		expect(edgeOf(canvas, "e1").sourceHandle).toBe("Reject");
		expect(edgeOf(canvas, "e2").sourceHandle).toBe("Approve");
		expect(edgeOf(canvas, "e3").sourceHandle).toBeUndefined();
	});

	it("leaves every other kind on the default handle", () => {
		const canvas = graphToCanvas(graph(nodes, [{ key: "e1", from: "plain", to: "done", label: "true" }]));

		expect(edgeOf(canvas, "e1").sourceHandle).toBeUndefined();
	});
});

describe("renameNodeKey", () => {
	it("rewrites the node id, its data key and every edge endpoint", () => {
		const canvas = graphToCanvas(eightNodeGraph);
		const result = renameNodeKey(canvas.nodes, canvas.edges, "review", "human-review");

		expect("error" in result).toBe(false);
		if ("error" in result) {
			return;
		}
		const renamed = result.nodes.find((node) => node.id === "human-review");
		expect(renamed?.data.key).toBe("human-review");
		expect(result.nodes.some((node) => node.id === "review")).toBe(false);
		expect(result.edges.find((edge) => edge.id === "e5")?.source).toBe("human-review");
		expect(result.edges.find((edge) => edge.id === "e3")?.target).toBe("human-review");
	});

	it("leaves a Pause out-edge's condition value alone — it is a decision, not a node key", () => {
		const canvas = graphToCanvas(eightNodeGraph);
		const result = renameNodeKey(canvas.nodes, canvas.edges, "review", "human-review");

		expect("error" in result).toBe(false);
		if ("error" in result) {
			return;
		}
		const approve = result.edges.find((edge) => edge.id === "e5");
		expect(approve?.source).toBe("human-review");
		expect(approve?.data?.condition?.value).toBe("Approve");
		expect(result.edges.find((edge) => edge.id === "e9")?.data?.condition?.value).toBe("Reject");
	});

	it("refuses a name held by another node or by an edge, and one outside the server's charset", () => {
		const canvas = graphToCanvas(eightNodeGraph);

		expect(renameNodeKey(canvas.nodes, canvas.edges, "review", "lookup")).toEqual({ error: "collision" });
		expect(renameNodeKey(canvas.nodes, canvas.edges, "review", "e1")).toEqual({ error: "collision" });
		expect(renameNodeKey(canvas.nodes, canvas.edges, "review", "human review")).toEqual({ error: "invalid" });
		expect(renameNodeKey(canvas.nodes, canvas.edges, "review", "")).toEqual({ error: "invalid" });
	});

	it("accepts a rename to the key the node already holds", () => {
		const canvas = graphToCanvas(eightNodeGraph);
		const result = renameNodeKey(canvas.nodes, canvas.edges, "review", "review");

		expect("error" in result).toBe(false);
		if ("error" in result) {
			return;
		}
		expect(result.nodes.some((node) => node.id === "review")).toBe(true);
	});
});

describe("defaultNodeData and key minting", () => {
	it("starts Agent and Tool nodes on three attempts and every other kind on one", () => {
		expect(defaultNodeData("Agent", "agent-1").maxAttempts).toBe(3);
		expect(defaultNodeData("Tool", "tool-1").maxAttempts).toBe(3);
		expect(defaultNodeData("Condition", "condition-1").maxAttempts).toBe(1);
		expect(defaultNodeData("Parallel", "parallel-1").maxAttempts).toBe(1);
	});

	it("seeds each kind with the defaults the plan fixes", () => {
		const pause = defaultNodeData("Pause", "pause-1");
		expect(pause).toMatchObject({ kind: "Pause", allowedDecisions: ["Approve", "Reject"], requireComment: false });
		expect(defaultNodeData("Agent", "agent-1")).toMatchObject({ includeUpstreamOutputs: true, instructions: "" });
		expect(defaultNodeData("End", "done")).toMatchObject({ outcome: "completed", resultPath: null });
		expect(defaultNodeData("Start", "start")).toMatchObject({ inputSchema: null, defaultInput: null });
		expect(defaultNodeData("Join", "merge")).toMatchObject({ kind: "Join", joinPolicy: "All", label: "" });
		expect(defaultNodeData("Condition", "condition-1")).toMatchObject({ path: null });
	});

	it("mints the lowest free integer over the ONE key namespace", () => {
		expect(mintNodeKey("Agent", ["agent-1", "agent-3"])).toBe("agent-2");
		expect(mintNodeKey("Agent", [])).toBe("agent-1");
		expect(mintEdgeKey(["e1", "e2"])).toBe("e3");
		// An edge may not take a key a node holds, so the caller passes both sets and `e2` is skipped.
		expect(mintEdgeKey(["e1", "e2", "agent-1"])).toBe("e3");
	});
});

describe("graphWorkflowsEqual", () => {
	it("is order-independent over nodes and edges", () => {
		const reversed: GraphWorkflowGraph = {
			schemaVersion: 1,
			nodes: [...(eightNodeGraph.nodes ?? [])].reverse(),
			edges: [...(eightNodeGraph.edges ?? [])].reverse(),
		};

		expect(graphWorkflowsEqual(reversed, eightNodeGraph)).toBe(true);
	});

	it("sees a moved node, unlike Preview, because this wire persists position", () => {
		const moved = clone();
		const node = (moved.nodes ?? [])[0];
		if (node) {
			node.position = { x: 40, y: 0 };
		}

		expect(graphWorkflowsEqual(moved, eightNodeGraph)).toBe(false);
	});

	it("reads absent, null and the parser's default as the same graph", () => {
		const explicit = graph(
			[{ key: "start", kind: "Start", label: "", position: { x: 0, y: 0 }, joinPolicy: "All", config: { defaultInput: null } }],
			[{ key: "e1", from: "start", to: "start", label: "" }],
		);
		const terse: GraphWorkflowGraph = {
			nodes: [{ key: "start", kind: "Start", position: { x: 0, y: 0 }, config: {} }],
			edges: [{ key: "e1", from: "start", to: "start" }],
		};

		expect(graphWorkflowsEqual(explicit, terse)).toBe(true);
	});

	it("does not read a missing position as the origin — a laid-out node is a real edit", () => {
		const placed = graph([{ key: "start", kind: "Start", position: { x: 0, y: 0 }, config: {} }], []);
		const unplaced = graph([{ key: "start", kind: "Start", config: {} }], []);

		expect(graphWorkflowsEqual(placed, unplaced)).toBe(false);
	});

	it("sees a changed edge label, sourceHandle and condition", () => {
		const base = graph(
			[{ key: "check", kind: "Condition", position: { x: 0, y: 0 }, config: { path: "a" } }],
			[{ key: "e1", from: "check", to: "check", label: "yes", sourceHandle: "true", condition: { op: "Eq", value: true } }],
		);

		for (const change of [{ label: "no" }, { sourceHandle: "false" }, { condition: { op: "Ne", value: true } }] as const) {
			const mutated = graph(base.nodes ?? [], [{ ...(base.edges ?? [])[0], ...change }]);
			expect(graphWorkflowsEqual(mutated, base), JSON.stringify(change)).toBe(false);
		}
	});

	it("treats two empty graphs as equal and an empty one as different from a populated one", () => {
		expect(graphWorkflowsEqual(undefined, { schemaVersion: 1, nodes: [], edges: [] })).toBe(true);
		expect(graphWorkflowsEqual(undefined, eightNodeGraph)).toBe(false);
	});

	it("sees a config edit inside a nested document that only differs by a null member", () => {
		const withNull = graph([{ key: "a", kind: "Tool", position: { x: 0, y: 0 }, config: { arguments: { path: null } } }], []);
		const without = graph([{ key: "a", kind: "Tool", position: { x: 0, y: 0 }, config: { arguments: {} } }], []);

		// A null INSIDE the operator's own document is data; only the config's own top-level members are defaulted away.
		expect(graphWorkflowsEqual(withNull, without)).toBe(false);
	});
});

describe("graphToCanvas on an empty graph", () => {
	it("returns empty arrays rather than throwing", () => {
		const canvas = graphToCanvas(undefined);

		expect(canvas.nodes).toEqual([]);
		expect(canvas.edges).toEqual([]);
		expect(NODE_SPACING_Y).toBeGreaterThan(0);
	});
});
