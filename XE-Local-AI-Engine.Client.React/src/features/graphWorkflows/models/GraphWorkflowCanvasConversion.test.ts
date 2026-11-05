// The wire ↔ canvas mapping, which is where a definition quietly loses an edge, a branch or an operator's formatting.
// The anchor case is the round trip over an eight-node graph the backend actually accepted, because a mapping that
// only agrees with itself proves nothing about what the server stores.

import { describe, expect, it } from "vitest";

import { defaultNodeData } from "@/features/graphWorkflows/models/GraphWorkflowAuthoring";
import { canvasToGraph, graphToCanvas } from "@/features/graphWorkflows/models/GraphWorkflowCanvasConversion";
import {
	type GraphWorkflowCanvas,
	type GraphWorkflowCanvasEdge,
	type GraphWorkflowCanvasNode,
	type GraphWorkflowCanvasNodeData,
	type GraphWorkflowLoadedCanvas,
	graphWorkflowNodeTypeByKind,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { agentConfigSchema } from "@/features/graphWorkflows/models/GraphWorkflowConfigSchemas";
import { graphWorkflowsEqual } from "@/features/graphWorkflows/models/GraphWorkflowEquality";
import { NODE_SPACING_Y, RANK_SPACING_X } from "@/features/graphWorkflows/models/GraphWorkflowLayout";
import type {
	GraphWorkflowGraph,
	GraphWorkflowGraphEdge,
	GraphWorkflowGraphNode,
	GraphWorkflowNodeKind,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { chatGraph, eightNodeGraph, wireGraph } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";

/** The Agent members `agentConfigSchema` needs beyond the one under test. */
const canvasAgentConfig = {
	agentDefinitionId: null,
	instructions: "Do the thing.",
	model: null,
	reasoningEffort: null,
	includeUpstreamOutputs: true,
};

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
	it("round-trips the eight-node graph with no issues and no drift", () => {
		const canvas = graphToCanvas(eightNodeGraph);
		const { graph: result, issues } = canvasToGraph(canvas.nodes, canvas.edges);

		expect(issues).toEqual([]);
		expect(graphWorkflowsEqual(result, eightNodeGraph)).toBe(true);
		expect(result.nodes).toHaveLength(8);
		expect(result.edges).toHaveLength(9);
	});

	it("saves a Standard graph byte for byte: no kind, no chat block, no chat flags", () => {
		// The document an editor save stores, then opened and saved again: the second save must not change a byte.
		const first = graphToCanvas(eightNodeGraph);
		const saved = canvasToGraph(first.nodes, first.edges, first.settings).graph;
		const reopened = graphToCanvas(saved);
		const { graph: result } = canvasToGraph(reopened.nodes, reopened.edges, reopened.settings);

		expect(reopened.settings).toEqual({ kind: "Standard" });
		expect(JSON.stringify(result)).toBe(JSON.stringify(saved));
		expect(JSON.stringify(saved)).not.toMatch(/"kind":"(Standard|Chat)"|"chat"|publishToChat|includeAttachments/);
	});

	it("carries a Chat graph's kind, chat block and every chat member through the round trip", () => {
		const canvas = graphToCanvas(chatGraph);
		const { graph: result, issues } = canvasToGraph(canvas.nodes, canvas.edges, canvas.settings);

		expect(issues).toEqual([]);
		expect(canvas.settings).toEqual({ kind: "Chat", chat: { acceptsAttachments: true } });
		expect(JSON.stringify(result)).toBe(JSON.stringify(chatGraph));
		expect(dataOfKind(canvas, "ask", "ChatInput").prompt).toBe("What should I build?");
		expect(dataOfKind(canvas, "classify", "DecisionModel")).toMatchObject({
			question: "Is this a coding request?",
			labels: ["coding", "general"],
			provider: "llm",
			model: null,
			inputBindings: [{ parameter: "request", path: "input.text" }],
		});
		expect(dataOfKind(canvas, "code", "LlmCall")).toMatchObject({ publishToChat: true, includeAttachments: true });
		// Absent stays absent: the End takes the parser's Chat-graph default rather than a written `true`.
		expect(dataOfKind(canvas, "done", "End").publishToChat).toBeUndefined();
	});

	describe("native DecisionModel members", () => {
		function withClassify(config: Record<string, unknown>): GraphWorkflowGraph {
			return {
				...chatGraph,
				nodes: (chatGraph.nodes ?? []).map((node) => (node.key === "classify" ? { ...node, config } : node)),
			};
		}
		function classifyConfig(canvas: GraphWorkflowLoadedCanvas): unknown {
			const { graph } = canvasToGraph(canvas.nodes, canvas.edges, canvas.settings);
			return graph.nodes?.find((node) => node.key === "classify")?.config;
		}

		it("round-trips a systemone choice with criteria and a score with ordered levels, verbatim", () => {
			for (const config of [
				{
					question: "Which team?",
					questionType: "choice",
					labels: ["billing", "shipping"],
					criteria: { billing: "payments, refunds" },
					provider: "systemone",
					model: "julia-1",
				},
				{ question: "How urgent?", questionType: "score", labels: ["low", "mid", "high"], provider: "systemone", model: "laya" },
			]) {
				expect(classifyConfig(graphToCanvas(withClassify(config)))).toEqual(config);
			}
		});

		it("drops an imported criteria key that names no label, so the node stays saveable", () => {
			const canvas = graphToCanvas(
				withClassify({
					question: "Which team?",
					labels: ["billing", "shipping"],
					criteria: { billing: "payments", support: "orphaned" },
					provider: "systemone",
					model: "julia-1",
				}),
			);
			expect(classifyConfig(canvas)).toMatchObject({ criteria: { billing: "payments" } });
		});

		it("keeps labels on the canvas for noul but leaves them, and blank criteria, off the wire", () => {
			const canvas = graphToCanvas(
				withClassify({ question: "Spam?", questionType: "noul", provider: "systemone", model: "laya" }),
			);
			const nodes = canvas.nodes.map((node) =>
				node.data.kind === "DecisionModel"
					? { ...node, data: { ...node.data, labels: ["kept"], criteria: { kept: "x" } } }
					: node,
			);
			expect(classifyConfig({ ...canvas, nodes })).toEqual({
				question: "Spam?",
				questionType: "noul",
				provider: "systemone",
				model: "laya",
			});

			const blank = canvas.nodes.map((node) =>
				node.data.kind === "DecisionModel"
					? { ...node, data: { ...node.data, questionType: null, labels: ["a", "b"], criteria: { a: " " } } }
					: node,
			);
			expect(classifyConfig({ ...canvas, nodes: blank })).toEqual({
				question: "Spam?",
				labels: ["a", "b"],
				provider: "systemone",
				model: "laya",
			});
		});
	});

	it("writes kind without a chat block when the Chat graph never set one, and drops both for Standard", () => {
		const canvas = graphToCanvas(eightNodeGraph);

		expect(canvasToGraph(canvas.nodes, canvas.edges, { kind: "Chat" }).graph).toMatchObject({ kind: "Chat" });
		expect("chat" in canvasToGraph(canvas.nodes, canvas.edges, { kind: "Chat" }).graph).toBe(false);
		const standard = canvasToGraph(canvas.nodes, canvas.edges, { kind: "Standard", chat: { acceptsAttachments: true } }).graph;
		expect("kind" in standard || "chat" in standard).toBe(false);
	});

	it("reads an absent kind and an explicit Standard as one graph, and a kind change as an edit", () => {
		expect(graphWorkflowsEqual(eightNodeGraph, { ...eightNodeGraph, kind: "Standard" })).toBe(true);
		expect(graphWorkflowsEqual(eightNodeGraph, { ...eightNodeGraph, kind: "Chat" })).toBe(false);
		expect(graphWorkflowsEqual(chatGraph, { ...chatGraph, chat: { acceptsAttachments: false } })).toBe(false);
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
		const source = structuredClone(eightNodeGraph);
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
	it("round-trips Start aliases trimmed, and omits the key when there are none", () => {
		const source = wireGraph(
			[
				{
					key: "start",
					kind: "Start",
					config: { inputSchema: null, defaultInput: null, aliases: ["lights on", "turn on the lights"] },
				},
			],
			[],
		);
		const canvas = graphToCanvas(source);
		expect(canvas.nodes[0]?.data).toMatchObject({ aliases: ["lights on", "turn on the lights"] });
		expect(canvasToGraph(canvas.nodes, canvas.edges).graph.nodes?.[0]?.config).toEqual(source.nodes?.[0]?.config);

		const edited = canvasToGraph(
			[
				canvasNode({
					...(defaultNodeData("Start", "start") as Extract<GraphWorkflowCanvasNodeData, { kind: "Start" }>),
					aliases: [" open word "],
				}),
			],
			[],
		);
		expect(edited.graph.nodes?.[0]?.config).toMatchObject({ aliases: ["open word"] });
		expect(canvasToGraph([canvasNode(defaultNodeData("Start", "start"))], []).graph.nodes?.[0]?.config).not.toHaveProperty(
			"aliases",
		);
	});

	it("round-trips an LLM Call and omits every unset override", () => {
		const freshConfig = canvasToGraph([canvasNode(defaultNodeData("LlmCall", "llm-call-1"))], []).graph.nodes?.[0]
			?.config as Record<string, unknown>;
		expect(freshConfig).toEqual({ prompt: "" });

		const source = wireGraph(
			[
				{
					key: "llm-call-1",
					kind: "LlmCall",
					config: {
						model: "qwen",
						systemPrompt: "",
						prompt: "Summarize",
						inputBindings: { source: "output.json" },
						reasoningEffort: "medium",
						responseJsonSchema: { type: "object" },
						samplingOptions: { temperature: 0.2, seed: "42", stop: ["END"] },
					},
				},
			],
			[],
		);
		const canvas = graphToCanvas(source);
		const result = canvasToGraph(canvas.nodes, canvas.edges);
		expect(result.issues).toEqual([]);
		expect(result.graph.nodes?.[0]?.config).toEqual(source.nodes?.[0]?.config);
	});

	it("keeps a precision-safe LLM seed string and rejects lossy binding maps", () => {
		const defaults = defaultNodeData("LlmCall", "llm-call-1") as Extract<GraphWorkflowCanvasNodeData, { kind: "LlmCall" }>;
		const data: Extract<GraphWorkflowCanvasNodeData, { kind: "LlmCall" }> = {
			...defaults,
			kind: "LlmCall",
			prompt: "Run",
			samplingOptions: { seed: "9223372036854775807" },
			inputBindings: [
				{ parameter: "source", path: "output.first" },
				{ parameter: "source", path: "output.second" },
			],
		};
		const result = canvasToGraph([canvasNode(data)], []);
		const config = result.graph.nodes?.[0]?.config as Record<string, unknown>;

		expect(config["samplingOptions"]).toEqual({ seed: "9223372036854775807" });
		expect(config).not.toHaveProperty("inputBindings");
		expect(result.issues).toContainEqual({ rule: "invalidInputBindings", subject: "llm-call-1" });
	});

	it("omits an empty LLM stop override when it is cleared", () => {
		const defaults = defaultNodeData("LlmCall", "llm-call-1") as Extract<GraphWorkflowCanvasNodeData, { kind: "LlmCall" }>;
		const result = canvasToGraph([canvasNode({ ...defaults, prompt: "Run", samplingOptions: { stop: [""] } })], []);
		const config = result.graph.nodes?.[0]?.config as Record<string, unknown>;

		expect(config).not.toHaveProperty("samplingOptions");
	});
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
					allowedUrls: [],
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

	it("round-trips a web_fetch node's allowedUrls and writes none for an empty list", () => {
		const listed = canvasToGraph(
			[
				canvasNode({
					...defaultNodeData("Tool", "fetch-1"),
					kind: "Tool",
					toolName: "web_fetch",
					argumentsJson: "",
					argumentBindings: [],
					allowedUrls: ["https://docs.example.com/guide"],
				}),
			],
			[],
		);
		const config = (listed.graph.nodes ?? [])[0]?.config as Record<string, unknown>;
		expect(config["allowedUrls"]).toEqual(["https://docs.example.com/guide"]);
		expect(dataOfKind(graphToCanvas(listed.graph), "fetch-1", "Tool").allowedUrls).toEqual(["https://docs.example.com/guide"]);

		const empty = canvasToGraph([canvasNode(defaultNodeData("Tool", "tool-1"))], []);
		expect(Object.keys((empty.graph.nodes ?? [])[0]?.config as Record<string, unknown>)).not.toContain("allowedUrls");
	});

	it("reads a malformed or missing config as the kind's defaults instead of throwing", () => {
		const canvas = graphToCanvas(
			wireGraph(
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
			aliases: [],
		});

		const { graph: result, issues } = canvasToGraph([start], []);

		expect(issues.map((issue) => issue.rule)).toEqual(["invalidJson"]);
		const config = (result.nodes ?? [])[0]?.config as Record<string, unknown>;
		expect(config["inputSchema"]).toBeNull();
		// `defaultInput` is `unknown` on the wire, so any JSON value survives.
		expect(config["defaultInput"]).toBe("a plain string is legal here");
	});

	it("round-trips a string-valued defaultInput unchanged", () => {
		// The wire member is a JSON STRING. Rendered verbatim it read as `abc`, which does not parse, so a
		// definition the server accepts carried a permanent invalidJson issue and a save rewrote the member to null.
		const canvas = graphToCanvas(wireGraph([{ key: "start", kind: "Start", config: { defaultInput: "abc" } }], []));

		expect(dataOfKind(canvas, "start", "Start").defaultInput).toBe('"abc"');

		const { graph: result, issues } = canvasToGraph(canvas.nodes, canvas.edges);

		expect(issues).toEqual([]);
		const config = (result.nodes ?? [])[0]?.config as Record<string, unknown>;
		expect(config["defaultInput"]).toBe("abc");
	});

	it("reports a string-valued responseJsonSchema as the wrong SHAPE, not as unparseable text", () => {
		const canvas = graphToCanvas(wireGraph([{ key: "agent-1", kind: "Agent", config: { responseJsonSchema: "abc" } }], []));
		const text = dataOfKind(canvas, "agent-1", "Agent").responseJsonSchema;

		expect(text).toBe('"abc"');
		// The drawer's own field check is what tells the two apart: the text parses, so it is `notObject`, and the
		// operator is told to enter an object rather than to fix JSON that is already valid.
		expect(agentConfigSchema.safeParse({ ...canvasAgentConfig, responseJsonSchema: text }).error?.issues[0]?.message).toBe(
			"pages.graphWorkflows.form.responseJsonSchema.notObject",
		);
		expect(canvasToGraph(canvas.nodes, canvas.edges).issues).toEqual([{ rule: "invalidJson", subject: "agent-1" }]);
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
	/** One Condition node fanning out to one End, with a conditional edge per operand: `e1`, `e2`, … in order. */
	function conditionGraph(conditions: readonly NonNullable<GraphWorkflowGraphEdge["condition"]>[]): GraphWorkflowGraph {
		return wireGraph(
			[
				{ key: "check", kind: "Condition", position: { x: 0, y: 0 }, config: { path: "output.json.status" } },
				{ key: "done", kind: "End", position: { x: 0, y: 120 }, config: { outcome: "completed", resultPath: null } },
			],
			conditions.map((condition, index) => ({ key: `e${index + 1}`, from: "check", to: "done", condition })),
		);
	}

	it("renders every value as its JSON text, so a string is quoted", () => {
		const canvas = graphToCanvas(eightNodeGraph);

		expect(edgeOf(canvas, "e5").data?.condition).toEqual({ path: "output.decision", op: "Eq", value: '"Approve"' });
		expect(edgeOf(canvas, "e3").data?.condition).toEqual({ op: "Eq", value: "true" });
	});

	// The bug the quoting exists for: the field renders, the operator saves, and the operand has changed TYPE — which
	// silently changes which branch the stored condition matches on the next run.
	it("round-trips a string operand that reads as another JSON type", () => {
		const operands = ["true", "123", "null", "Approve"];
		const source = conditionGraph(operands.map((value) => ({ op: "Ne", value })));

		const canvas = graphToCanvas(source);
		expect(canvas.edges.map((edge) => edge.data?.condition?.value)).toEqual(['"true"', '"123"', '"null"', '"Approve"']);

		const { graph: result } = canvasToGraph(canvas.nodes, canvas.edges);
		expect((result.edges ?? []).map((edge) => edge.condition?.value)).toEqual(operands);
	});

	// The other half of the rule: reading is strict JSON, writing stays lenient, so an operator who types a bare
	// word still gets a string and one who types a number still gets a number.
	it("saves unquoted text as a string and an unquoted number as a number", () => {
		const canvas = graphToCanvas(
			conditionGraph([
				{ op: "Ne", value: 0 },
				{ op: "Ne", value: 0 },
			]),
		);
		// What the operator typed into the value field, which is the only way a canvas condition value is authored.
		const typed = canvas.edges.map((edge) => ({
			...edge,
			data: { ...edge.data, condition: { op: "Ne" as const, value: edge.id === "e1" ? "Approve" : "123" } },
		}));

		const { graph: result } = canvasToGraph(canvas.nodes, typed);
		expect(wireEdge(result, "e1").condition?.value).toBe("Approve");
		expect(wireEdge(result, "e2").condition?.value).toBe(123);
	});

	it("normalises a stored lowercase operator to its canonical member and writes the canonical one back", () => {
		const source = wireGraph(
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
		// The two spellings are the same branch: the dirty check normalizes the token as the server's parser does, so
		// opening a graph stored with `eq` does not report an edit that was never made.
		const canonical = wireGraph(source.nodes ?? [], [{ ...(source.edges ?? [])[0], condition: { op: "Eq", value: true } }]);
		expect(graphWorkflowsEqual(canonical, source)).toBe(true);
		// The handle IS re-derived, though, and persisting it is a real (and intended) edit.
		expect(wireEdge(result, "e1").sourceHandle).toBe("true");
	});

	it("drops a condition whose operator it cannot read rather than guessing one", () => {
		const source = wireGraph(
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
		const source = wireGraph(
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
		const canvas = graphToCanvas(
			wireGraph(nodes, [{ key: "e1", from: "check", to: "done", sourceHandle: "false", label: "yes" }]),
		);

		expect(edgeOf(canvas, "e1").sourceHandle).toBe("false");
	});

	it("re-derives a Condition handle from the label, then from an Eq boolean condition", () => {
		const canvas = graphToCanvas(
			wireGraph(nodes, [
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
			wireGraph(nodes, [
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
		const canvas = graphToCanvas(wireGraph(nodes, [{ key: "e1", from: "plain", to: "done", label: "true" }]));

		expect(edgeOf(canvas, "e1").sourceHandle).toBeUndefined();
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
