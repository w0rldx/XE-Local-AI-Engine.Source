// The save gate, rule by rule. For every rule the test that matters is the same pair: the shape the SERVER rejects is
// reported here, and the shape it accepts produces nothing. The second half is the one that earns its keep — a mirror
// that refuses graphs the server would take blocks a save outright, which is the one direction that cannot be worked
// around from the UI.

import { describe, expect, it } from "vitest";

import type {
	GraphWorkflowGraph,
	GraphWorkflowGraphEdge,
	GraphWorkflowGraphNode,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import {
	agentConfigSchema,
	conditionConfigSchema,
	edgeConditionSchema,
	endConfigSchema,
	type GraphWorkflowGraphRule,
	graphWorkflowGraphRules,
	loadedGraphIssues,
	nodeCommonSchema,
	pauseConfigSchema,
	serverErrorsToIssues,
	startConfigSchema,
	toolConfigSchema,
	validateGraphWorkflowGraph,
} from "@/features/graphWorkflows/models/GraphWorkflowValidation";
import { eightNodeGraph } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import en from "@/locales/en.json";

function graph(nodes: GraphWorkflowGraphNode[], edges: GraphWorkflowGraphEdge[] = []): GraphWorkflowGraph {
	return { schemaVersion: 1, nodes, edges };
}

/** `start → done`: the smallest graph the server accepts, and the shape every negative case below is a mutation of. */
function minimal(nodes: GraphWorkflowGraphNode[] = [], edges: GraphWorkflowGraphEdge[] = []): GraphWorkflowGraph {
	return graph(
		[{ key: "start", kind: "Start", config: {} }, { key: "done", kind: "End", config: { outcome: "completed" } }, ...nodes],
		[{ key: "e1", from: "start", to: "done" }, ...edges],
	);
}

function rulesOf(candidate: GraphWorkflowGraph | undefined): GraphWorkflowGraphRule[] {
	return validateGraphWorkflowGraph(candidate).map((issue) => issue.rule);
}

/** `progress/S2-live/pause-tool-graph.json`: the body the backend actually accepted in the S2 live round. */
function liveGraph(): GraphWorkflowGraph {
	return graph(
		[
			{ key: "start", kind: "Start", config: {} },
			{
				key: "review",
				kind: "Pause",
				config: { prompt: "Look up the time?", allowedDecisions: ["Approve", "Reject"], requireComment: false },
			},
			{ key: "lookup", kind: "Tool", config: { toolName: "GetCurrentTime", arguments: {} } },
			{ key: "done", kind: "End", joinPolicy: "Any", config: { outcome: "completed" } },
		],
		[
			{ key: "e1", from: "start", to: "review" },
			{
				key: "e2",
				from: "review",
				to: "lookup",
				label: "approved",
				condition: { path: "output.decision", op: "eq", value: "Approve" },
			},
			{
				key: "e3",
				from: "review",
				to: "done",
				label: "rejected",
				condition: { path: "output.decision", op: "eq", value: "Reject" },
			},
			{ key: "e4", from: "lookup", to: "done" },
		],
	);
}

describe("validateGraphWorkflowGraph accepts what the server accepts", () => {
	it("reports nothing for the graph the backend accepted in the S2 live round", () => {
		expect(validateGraphWorkflowGraph(liveGraph())).toEqual([]);
	});

	it("reports nothing for the shared eight-node fixture", () => {
		expect(validateGraphWorkflowGraph(eightNodeGraph)).toEqual([]);
	});

	it("reports the unrouted decision the moment the Pause node's Reject edge is taken away", () => {
		// The brief's §3.1 listing draws only the Approve edge, which is why the fixture had to add the other one: the
		// server refuses a Pause whose allowed decision no out-edge routes, and this is that rule's client mirror.
		const unwired = graph(
			eightNodeGraph.nodes ?? [],
			(eightNodeGraph.edges ?? []).filter((edge) => edge.key !== "e9"),
		);

		// Dropping it also leaves the reconverging End with one inbound edge, which its `Any` join no longer needs.
		expect(validateGraphWorkflowGraph(unwired)).toEqual([
			{ rule: "pauseDecisionUnroutable", subject: "review" },
			{ rule: "joinAnyNeedsTwoInbound", subject: "done" },
		]);
	});

	it("reports nothing for the smallest legal graph, and nothing at all when no graph is loaded", () => {
		expect(validateGraphWorkflowGraph(minimal())).toEqual([]);
		expect(validateGraphWorkflowGraph(undefined)).toEqual([]);
		// An empty node list IS a broken graph, unlike an absent one.
		expect(rulesOf(graph([]))).toEqual(["noStart", "noEnd"]);
	});

	it("accepts two parallel edges over the same pair when one of them carries a condition", () => {
		const parallel = minimal(
			[{ key: "check", kind: "Condition", config: { path: "output.json.ok" } }],
			[
				{ key: "e2", from: "start", to: "check" },
				{ key: "e3", from: "check", to: "done", condition: { op: "Eq", value: true } },
				{ key: "e4", from: "check", to: "done" },
			],
		);

		expect(validateGraphWorkflowGraph(parallel)).toEqual([]);
	});

	it("accepts a conditional edge with no path when its source Condition node carries one", () => {
		const inherited = minimal(
			[{ key: "check", kind: "Condition", config: { path: "output.json.ok" } }],
			[
				{ key: "e2", from: "start", to: "check" },
				{ key: "e3", from: "check", to: "done", condition: { op: "Eq", value: true } },
				{ key: "e4", from: "check", to: "done", condition: { op: "Ne", value: true } },
			],
		);

		expect(validateGraphWorkflowGraph(inherited)).toEqual([]);
	});

	it("accepts a Pause whose every allowed decision has a matching or unconditional out-edge", () => {
		const pause = minimal(
			[{ key: "review", kind: "Pause", config: { prompt: "?", allowedDecisions: ["Approve", "Reject"] } }],
			[
				{ key: "e2", from: "start", to: "review" },
				{ key: "e3", from: "review", to: "done", condition: { path: "output.decision", op: "eq", value: "Approve" } },
				{ key: "e4", from: "review", to: "done", condition: { path: "output.decision", op: "Eq", value: "Reject" } },
			],
		);

		// The wire token is compared case-insensitively, as the server parses it — the S2 live graph stored `eq`.
		expect(validateGraphWorkflowGraph(pause)).toEqual([]);
	});
});

describe("validateGraphWorkflowGraph reports one failing case per rule", () => {
	it("duplicateNodeKey", () => {
		const duplicate = minimal([{ key: "done", kind: "Agent", config: {} }], [{ key: "e2", from: "done", to: "done" }]);

		expect(rulesOf(duplicate)).toContain("duplicateNodeKey");
	});

	it("missingNodeKey", () => {
		expect(rulesOf(minimal([{ kind: "Agent", config: {} }]))).toContain("missingNodeKey");
	});

	it("invalidNodeKey", () => {
		const invalid = minimal([{ key: "not a key", kind: "Agent", config: {} }]);

		expect(rulesOf(invalid)).toContain("invalidNodeKey");
	});

	it("unknownEdgeEndpoint", () => {
		expect(rulesOf(minimal([], [{ key: "e2", from: "start", to: "nowhere" }]))).toContain("unknownEdgeEndpoint");
	});

	it("duplicateEdgeKey, whether the collision is with another edge or with a node", () => {
		expect(rulesOf(minimal([], [{ key: "e1", from: "start", to: "done" }]))).toContain("duplicateEdgeKey");
		expect(rulesOf(minimal([], [{ key: "start", from: "start", to: "done" }]))).toContain("duplicateEdgeKey");
	});

	it("missingEdgeKey and invalidEdgeKey", () => {
		expect(rulesOf(minimal([], [{ from: "start", to: "done" }]))).toContain("missingEdgeKey");
		expect(rulesOf(minimal([], [{ key: "not an edge", from: "start", to: "done" }]))).toContain("invalidEdgeKey");
	});

	it("parallelEdgesBothUnconditional", () => {
		expect(rulesOf(minimal([], [{ key: "e2", from: "start", to: "done" }]))).toContain("parallelEdgesBothUnconditional");
	});

	it("conditionEdgeHasNoPath, for a conditional edge whose source is not a Condition node", () => {
		const pathless = minimal([], [{ key: "e2", from: "start", to: "done", condition: { op: "Exists" } }]);

		expect(rulesOf(pathless)).toContain("conditionEdgeHasNoPath");
	});

	it("noStart", () => {
		expect(rulesOf(graph([{ key: "done", kind: "End", config: {} }]))).toContain("noStart");
	});

	it("multipleStarts", () => {
		expect(rulesOf(minimal([{ key: "start-2", kind: "Start", config: {} }]))).toContain("multipleStarts");
	});

	it("noEnd", () => {
		expect(rulesOf(graph([{ key: "start", kind: "Start", config: {} }]))).toContain("noEnd");
	});

	it("cycle", () => {
		const cyclic = minimal(
			[{ key: "a", kind: "Agent", config: {} }],
			[
				{ key: "e2", from: "start", to: "a" },
				{ key: "e3", from: "a", to: "a" },
			],
		);

		expect(rulesOf(cyclic)).toContain("cycle");
	});

	it("unreachable", () => {
		const stranded = minimal([{ key: "island", kind: "Agent", config: {} }], [{ key: "e2", from: "island", to: "done" }]);

		expect(rulesOf(stranded)).toContain("unreachable");
	});

	it("danglingNonEnd", () => {
		const dangling = minimal([{ key: "tail", kind: "Agent", config: {} }], [{ key: "e2", from: "start", to: "tail" }]);

		expect(rulesOf(dangling)).toContain("danglingNonEnd");
	});

	it("endHasOutbound", () => {
		const talkative = minimal(
			[{ key: "after", kind: "Agent", config: {} }],
			[
				{ key: "e2", from: "done", to: "after" },
				{ key: "e3", from: "after", to: "done" },
			],
		);

		expect(rulesOf(talkative)).toContain("endHasOutbound");
	});

	it("startHasInbound", () => {
		const looped = minimal(
			[{ key: "a", kind: "Agent", config: {} }],
			[
				{ key: "e2", from: "start", to: "a" },
				{ key: "e3", from: "a", to: "start" },
			],
		);

		expect(rulesOf(looped)).toContain("startHasInbound");
	});

	it("joinAnyNeedsTwoInbound", () => {
		const lonely = minimal(
			[{ key: "merge", kind: "Join", joinPolicy: "Any", config: {} }],
			[
				{ key: "e2", from: "start", to: "merge" },
				{ key: "e3", from: "merge", to: "done" },
			],
		);

		expect(rulesOf(lonely)).toContain("joinAnyNeedsTwoInbound");
	});

	it("conditionNeedsTwoOutbound", () => {
		const single = minimal(
			[{ key: "check", kind: "Condition", config: { path: "a" } }],
			[
				{ key: "e2", from: "start", to: "check" },
				{ key: "e3", from: "check", to: "done", condition: { op: "Eq", value: true } },
			],
		);

		expect(rulesOf(single)).toContain("conditionNeedsTwoOutbound");
	});

	it("conditionMultipleDefaults", () => {
		const ambiguous = minimal(
			[
				{ key: "check", kind: "Condition", config: { path: "a" } },
				{ key: "other", kind: "End", config: { outcome: "other" } },
			],
			[
				{ key: "e2", from: "start", to: "check" },
				{ key: "e3", from: "check", to: "done" },
				{ key: "e4", from: "check", to: "other" },
			],
		);

		expect(rulesOf(ambiguous)).toContain("conditionMultipleDefaults");
	});

	it("tooManyNodes", () => {
		const crowded = minimal(Array.from({ length: 200 }, (_, index) => ({ key: `agent-${index}`, kind: "Agent", config: {} })));

		expect(rulesOf(crowded)).toContain("tooManyNodes");
	});

	it("invalidJson, when a config member the wire needs as an object is not one", () => {
		const notAnObject = minimal([{ key: "a", kind: "Agent", config: { instructions: "x", responseJsonSchema: "{ half-typed" } }]);

		expect(rulesOf(notAnObject)).toContain("invalidJson");
	});

	it("pauseDecisionUnroutable", () => {
		const unwired = minimal(
			[{ key: "review", kind: "Pause", config: { prompt: "?", allowedDecisions: ["Approve", "Reject"] } }],
			[
				{ key: "e2", from: "start", to: "review" },
				{ key: "e3", from: "review", to: "done", condition: { path: "output.decision", op: "Eq", value: "Approve" } },
			],
		);

		expect(rulesOf(unwired)).toContain("pauseDecisionUnroutable");
	});

	it("unknownNodeKind", () => {
		expect(rulesOf(minimal([{ key: "mystery", kind: "Transform", config: {} }]))).toContain("unknownNodeKind");
	});

	it("unknownConditionOperator", () => {
		const nonsense = minimal(
			[],
			[{ key: "e2", from: "start", to: "done", condition: { path: "a", op: "approximately", value: 1 } }],
		);

		expect(rulesOf(nonsense)).toContain("unknownConditionOperator");
	});

	it("agentInstructionsMissing", () => {
		expect(rulesOf(minimal([{ key: "a", kind: "Agent", config: { instructions: "  " } }]))).toContain("agentInstructionsMissing");
	});

	it("pausePromptMissing and pauseNoDecisions", () => {
		const bare = rulesOf(minimal([{ key: "review", kind: "Pause", config: { prompt: "", allowedDecisions: [] } }]));

		expect(bare).toContain("pausePromptMissing");
		expect(bare).toContain("pauseNoDecisions");
	});

	it("endOutcomeMissing", () => {
		expect(
			rulesOf(
				graph([
					{ key: "start", kind: "Start", config: {} },
					{ key: "done", kind: "End", config: {} },
				]),
			),
		).toContain("endOutcomeMissing");
	});

	it("toolNameMissing", () => {
		const nameless = minimal(
			[{ key: "lookup", kind: "Tool", config: { arguments: {} } }],
			[
				{ key: "e2", from: "start", to: "lookup" },
				{ key: "e3", from: "lookup", to: "done" },
			],
		);

		expect(rulesOf(nameless)).toContain("toolNameMissing");
	});
});

describe("loadedGraphIssues", () => {
	it("keeps only what the canvas cannot round-trip, and nothing else the graph is guilty of", () => {
		const lossy = graph(
			[
				{ key: "start", kind: "Start", config: {} },
				{ key: "mystery", kind: "Transform", config: {} },
				{ key: "done", kind: "End", config: { outcome: "completed" } },
			],
			[
				{ key: "e1", from: "start", to: "done", condition: { path: "a", op: "approximately", value: 1 } },
				{ key: "e2", from: "mystery", to: "done" },
			],
		);

		expect(loadedGraphIssues(lossy)).toEqual([
			{ rule: "unknownNodeKind", subject: "mystery" },
			{ rule: "unknownConditionOperator", subject: "e1" },
		]);
		// The same graph is guilty of plenty more; only the two lossy ones are held open against the loaded document.
		expect(rulesOf(lossy).length).toBeGreaterThan(2);
	});

	it("finds nothing in a graph this client can write back faithfully", () => {
		expect(loadedGraphIssues(eightNodeGraph)).toEqual([]);
		expect(loadedGraphIssues(undefined)).toEqual([]);
	});
});

describe("serverErrorsToIssues", () => {
	it("maps a keyed error to its subject and an unkeyed one to none, through the same shape", () => {
		const issues = serverErrorsToIssues([
			{ key: "analyze", message: "The Agent node has no instructions." },
			{ key: null, message: "The graph document could not be read." },
		]);

		expect(issues).toEqual([
			{ rule: "serverRejected", subject: "analyze", message: "The Agent node has no instructions." },
			{ rule: "serverRejected", subject: undefined, message: "The graph document could not be read." },
		]);
	});

	it("answers an empty list when the server sent no errors", () => {
		expect(serverErrorsToIssues(undefined)).toEqual([]);
	});
});

describe("every rule has an English message", () => {
	it("names a string under pages.graphWorkflows.definition.issues for each member", () => {
		const issues = (en as { pages: { graphWorkflows: { definition: { issues: Record<string, unknown> } } } }).pages.graphWorkflows
			.definition.issues;

		for (const rule of graphWorkflowGraphRules) {
			expect(issues[rule], rule).toBeTypeOf("string");
		}
	});
});

describe("config form schemas", () => {
	it("accepts the eight-node graph's own config shapes", () => {
		expect(startConfigSchema.safeParse({ inputSchema: null, defaultInput: null }).success).toBe(true);
		expect(
			agentConfigSchema.safeParse({
				agentDefinitionId: null,
				instructions: "Summarise it.",
				model: null,
				reasoningEffort: null,
				responseJsonSchema: '{ "type": "object" }',
				includeUpstreamOutputs: true,
			}).success,
		).toBe(true);
		expect(toolConfigSchema.safeParse({ toolName: "read_file", argumentsJson: "", argumentBindings: [] }).success).toBe(true);
		expect(conditionConfigSchema.safeParse({ path: "output.json.requiresReview" }).success).toBe(true);
		// The server's `IsDotPath` accepts any segment without whitespace or wildcard punctuation, and a JSON property
		// really can be hyphenated — refusing one it accepts would block a save the node would have taken.
		expect(conditionConfigSchema.safeParse({ path: "output.json.requires-review" }).success).toBe(true);
		expect(
			pauseConfigSchema.safeParse({ prompt: "Approve?", allowedDecisions: ["Approve"], requireComment: false }).success,
		).toBe(true);
		expect(endConfigSchema.safeParse({ outcome: "completed", resultPath: null }).success).toBe(true);
		expect(nodeCommonSchema.safeParse({ key: "analyze", label: "Analyze", maxAttempts: 3, timeoutSeconds: null }).success).toBe(
			true,
		);
	});

	it("answers an i18n KEY, not a sentence, for every field it refuses", () => {
		const cases = [
			[nodeCommonSchema, { key: "not a key", label: "" }, "pages.graphWorkflows.form.key.invalid"],
			[nodeCommonSchema, { key: "a", label: "", maxAttempts: 101 }, "pages.graphWorkflows.form.maxAttempts.range"],
			[nodeCommonSchema, { key: "a", label: "", timeoutSeconds: 0 }, "pages.graphWorkflows.form.timeoutSeconds.min"],
			[startConfigSchema, { inputSchema: "[1]", defaultInput: null }, "pages.graphWorkflows.form.inputSchema.notObject"],
			[startConfigSchema, { inputSchema: null, defaultInput: "{ half" }, "pages.graphWorkflows.form.defaultInput.invalidJson"],
			[
				agentConfigSchema,
				{
					agentDefinitionId: null,
					instructions: "  ",
					model: null,
					reasoningEffort: null,
					responseJsonSchema: null,
					includeUpstreamOutputs: false,
				},
				"pages.graphWorkflows.form.instructions.required",
			],
			[
				agentConfigSchema,
				{
					agentDefinitionId: null,
					instructions: "x",
					model: null,
					reasoningEffort: null,
					responseJsonSchema: "3",
					includeUpstreamOutputs: false,
				},
				"pages.graphWorkflows.form.responseJsonSchema.notObject",
			],
			[
				toolConfigSchema,
				{ toolName: null, argumentsJson: "", argumentBindings: [] },
				"pages.graphWorkflows.form.toolName.required",
			],
			[
				toolConfigSchema,
				{ toolName: "read_file", argumentsJson: "nope", argumentBindings: [] },
				"pages.graphWorkflows.form.argumentsJson.notObject",
			],
			[
				toolConfigSchema,
				{ toolName: "read_file", argumentsJson: "", argumentBindings: [{ parameter: "path", path: " " }] },
				"pages.graphWorkflows.form.argumentBindings.pathRequired",
			],
			[conditionConfigSchema, { path: "output[0].json" }, "pages.graphWorkflows.form.path.invalid"],
			[
				pauseConfigSchema,
				{ prompt: "?", allowedDecisions: [], requireComment: false },
				"pages.graphWorkflows.form.allowedDecisions.required",
			],
			[
				pauseConfigSchema,
				{ prompt: " ", allowedDecisions: ["Approve"], requireComment: false },
				"pages.graphWorkflows.form.prompt.required",
			],
			[endConfigSchema, { outcome: "", resultPath: null }, "pages.graphWorkflows.form.outcome.required"],
			[endConfigSchema, { outcome: "done", resultPath: "a b" }, "pages.graphWorkflows.form.resultPath.invalid"],
			[edgeConditionSchema, { op: "Eq", value: "" }, "pages.graphWorkflows.form.condition.valueRequired"],
			[edgeConditionSchema, { op: "Eq", value: "1", path: "a b" }, "pages.graphWorkflows.form.path.invalid"],
		] as const;

		for (const [schema, value, expected] of cases) {
			const result = schema.safeParse(value);
			expect(result.success, expected).toBe(false);
			expect(
				result.error?.issues.map((issue) => issue.message),
				expected,
			).toContain(expected);
		}
	});

	it("takes no value for Exists and NotExists, which have no operand", () => {
		expect(edgeConditionSchema.safeParse({ op: "Exists", value: "" }).success).toBe(true);
		expect(edgeConditionSchema.safeParse({ op: "NotExists", value: "" }).success).toBe(true);
	});
});
