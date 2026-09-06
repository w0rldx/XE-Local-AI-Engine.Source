// The feature's one fixture file, copied in shape from `devWorkflows/test/DevWorkflowFixtures.ts`: per-DTO builders
// rather than per-route, so one run payload backs several test files parameterised by node status.
//
// Every builder returns the WIRE shape (all fields optional, as hey-api types them from the OpenAPI document), so a
// test can spread overrides in without fighting a stricter local type than the server actually promises. The graph is
// `00-brief.md` §3.1 — eight nodes and nine edges — because a fixture that
// diverges from the real body is a test that proves the client agrees with itself.
//
// Timestamps are epoch MILLISECONDS (numbers), as every Graph Workflows DTO carries them.

import type {
	GraphWorkflowDefinitionResponse,
	GraphWorkflowDefinitionSummaryResponse,
	GraphWorkflowGraph,
	GraphWorkflowNodeRunResponse,
	GraphWorkflowNodeRunSummaryResponse,
	GraphWorkflowRunEventResponse,
	GraphWorkflowRunResponse,
	GraphWorkflowRunSummaryResponse,
	ListGraphWorkflowRunEventsResponse,
	ListGraphWorkflowToolsResponse,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

export const graphWorkflowTestIds = {
	definition: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
	run: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
	nodeRun: "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
	request: "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
} as const;

export const graphWorkflowTestGraphHash = "sha256:0f2a9c1d4e6b8a70";

/**
 * The brief's §3.1 graph: Start → Agent → Condition → { Pause | Tool } → Parallel → Join → End. Eight nodes, nine
 * edges — the plan calls it "the six-node definition" but the graph it points at has eight, and the graph wins.
 *
 * The two Condition out-edges carry `sourceHandle` `true`/`false` with `Eq true` / `Ne true` and no `path` (they
 * inherit the Condition node's `config.path`).
 *
 * The Pause node has TWO out-edges, one per entry in its `allowedDecisions`. The brief's own §3.1 listing draws only
 * the `Approve` one, which breaks the Pause pre-flight rule the same section states: the server refuses a Pause whose
 * allowed decision has no matching or unconditional out-edge. `e9` is the missing `Reject` edge, and it makes `done` a
 * reconverging End, which needs `joinPolicy: "Any"` or the approve path is skipped on the dead reject edge. Both
 * shapes are the ones the S2 live graph uses.
 */
export const eightNodeGraph: GraphWorkflowGraph = {
	schemaVersion: 1,
	nodes: [
		{
			key: "start",
			kind: "Start",
			label: "Start",
			position: { x: 0, y: 0 },
			config: { inputSchema: null, defaultInput: null },
		},
		{
			key: "analyze",
			kind: "Agent",
			label: "Analyze",
			position: { x: 0, y: 120 },
			maxAttempts: 3,
			timeoutSeconds: null,
			config: {
				agentDefinitionId: null,
				instructions: "Summarise the request and say whether it needs a human review.",
				model: null,
				reasoningEffort: null,
				responseJsonSchema: { type: "object", properties: { requiresReview: { type: "boolean" } } },
				includeUpstreamOutputs: true,
			},
		},
		{
			key: "check",
			kind: "Condition",
			label: "Needs review?",
			position: { x: 0, y: 240 },
			config: { path: "output.json.requiresReview" },
		},
		{
			key: "review",
			kind: "Pause",
			label: "Human review",
			position: { x: -120, y: 360 },
			config: { prompt: "Approve the analysis?", allowedDecisions: ["Approve", "Reject"], requireComment: false },
		},
		{
			key: "lookup",
			kind: "Tool",
			label: "Read file",
			position: { x: 120, y: 360 },
			maxAttempts: 3,
			config: {
				toolName: "read_file",
				arguments: { path: "notes.md" },
				argumentBindings: { path: "output.json.path" },
			},
		},
		// `Any`, like `done`: the Condition's two branches are exclusive, so under the default `All` join this node would
		// wait forever for the branch that was never taken and the run could never reach an End.
		{ key: "fanout", kind: "Parallel", label: "Both", position: { x: 0, y: 480 }, joinPolicy: "Any", config: {} },
		{ key: "merge", kind: "Join", label: "Merge", position: { x: 0, y: 600 }, joinPolicy: "All", config: {} },
		{
			key: "done",
			kind: "End",
			label: "Done",
			position: { x: 0, y: 720 },
			joinPolicy: "Any",
			config: { outcome: "completed", resultPath: null },
		},
	],
	edges: [
		{ key: "e1", from: "start", to: "analyze" },
		{ key: "e2", from: "analyze", to: "check" },
		{ key: "e3", from: "check", to: "review", label: "yes", sourceHandle: "true", condition: { op: "Eq", value: true } },
		{ key: "e4", from: "check", to: "lookup", label: "no", sourceHandle: "false", condition: { op: "Ne", value: true } },
		{
			key: "e5",
			from: "review",
			to: "fanout",
			label: "approved",
			sourceHandle: "Approve",
			condition: { path: "output.decision", op: "Eq", value: "Approve" },
		},
		{ key: "e6", from: "lookup", to: "fanout" },
		{ key: "e7", from: "fanout", to: "merge" },
		{ key: "e8", from: "merge", to: "done" },
		{
			key: "e9",
			from: "review",
			to: "done",
			label: "rejected",
			sourceHandle: "Reject",
			condition: { path: "output.decision", op: "Eq", value: "Reject" },
		},
	],
};

export function graphWorkflowDefinition(
	overrides: Partial<GraphWorkflowDefinitionResponse> = {},
): GraphWorkflowDefinitionResponse {
	return {
		id: graphWorkflowTestIds.definition,
		name: "Analyze → review → read",
		description: "An agent decides, a human approves, a tool reads.",
		graph: eightNodeGraph,
		graphHash: graphWorkflowTestGraphHash,
		nodeCount: 8,
		schemaVersion: 1,
		version: 1,
		createdAtUtc: 1_700_000_000_000,
		updatedAtUtc: 1_700_000_100_000,
		...overrides,
	};
}

export function graphWorkflowDefinitionSummary(
	overrides: Partial<GraphWorkflowDefinitionSummaryResponse> = {},
): GraphWorkflowDefinitionSummaryResponse {
	return {
		id: graphWorkflowTestIds.definition,
		name: "Analyze → review → read",
		description: "An agent decides, a human approves, a tool reads.",
		graphHash: graphWorkflowTestGraphHash,
		nodeCount: 8,
		schemaVersion: 1,
		version: 1,
		createdAtUtc: 1_700_000_000_000,
		updatedAtUtc: 1_700_000_100_000,
		...overrides,
	};
}

export function graphWorkflowRunSummary(
	overrides: Partial<GraphWorkflowRunSummaryResponse> = {},
): GraphWorkflowRunSummaryResponse {
	return {
		id: graphWorkflowTestIds.run,
		requestId: graphWorkflowTestIds.request,
		definitionId: graphWorkflowTestIds.definition,
		definitionVersion: 1,
		graphHash: graphWorkflowTestGraphHash,
		status: "Running",
		failureClass: "None",
		cancelRequestedAtUtc: null,
		startedAtUtc: 1_700_000_200_000,
		completedAtUtc: null,
		createdAtUtc: 1_700_000_200_000,
		...overrides,
	};
}

export function makeNodeRun(overrides: Partial<GraphWorkflowNodeRunSummaryResponse> = {}): GraphWorkflowNodeRunSummaryResponse {
	return {
		id: graphWorkflowTestIds.nodeRun,
		nodeKey: "analyze",
		kind: "Agent",
		status: "Succeeded",
		attempt: 1,
		failureClass: "None",
		pendingDecisionKind: null,
		invocationId: null,
		startedAtUtc: 1_700_000_200_000,
		completedAtUtc: 1_700_000_210_000,
		updatedAtUtc: 1_700_000_210_000,
		...overrides,
	};
}

/**
 * A run parked on the Pause node, with one node run per node key — the server materializes every node of the pinned
 * graph at run start, so a run view that only shows started nodes would be showing a different graph.
 *
 * The mix is deliberately realistic rather than uniform: the Tool node is `Failed` with `AttemptsExhausted` (the state
 * the row shows even when the `Timeout` class only appears on the retry event) and the Join is `Skipped`, so a test can
 * assert a failed node stays failed inside a run that is still going.
 */
export function graphWorkflowRun(overrides: Partial<GraphWorkflowRunResponse> = {}): GraphWorkflowRunResponse {
	return {
		run: graphWorkflowRunSummary(),
		nodeRuns: [
			makeNodeRun({ id: "nr-start", nodeKey: "start", kind: "Start", status: "Succeeded" }),
			makeNodeRun({ id: "nr-analyze", nodeKey: "analyze", kind: "Agent", status: "Succeeded" }),
			makeNodeRun({ id: "nr-check", nodeKey: "check", kind: "Condition", status: "Succeeded" }),
			makeNodeRun({
				id: graphWorkflowTestIds.nodeRun,
				nodeKey: "review",
				kind: "Pause",
				status: "WaitingForApproval",
				pendingDecisionKind: "Approve",
				completedAtUtc: null,
			}),
			makeNodeRun({
				id: "nr-lookup",
				nodeKey: "lookup",
				kind: "Tool",
				status: "Failed",
				attempt: 3,
				failureClass: "AttemptsExhausted",
			}),
			makeNodeRun({
				id: "nr-fanout",
				nodeKey: "fanout",
				kind: "Parallel",
				status: "Pending",
				startedAtUtc: null,
				completedAtUtc: null,
			}),
			makeNodeRun({ id: "nr-merge", nodeKey: "merge", kind: "Join", status: "Skipped", startedAtUtc: null }),
			makeNodeRun({ id: "nr-done", nodeKey: "done", kind: "End", status: "Pending", startedAtUtc: null, completedAtUtc: null }),
		],
		output: undefined,
		...overrides,
	};
}

/** The Pause node's detail: what the decision panel reads to decide whether to render any control at all. */
export function pendingPauseNodeRun(overrides: Partial<GraphWorkflowNodeRunResponse> = {}): GraphWorkflowNodeRunResponse {
	return {
		id: graphWorkflowTestIds.nodeRun,
		runId: graphWorkflowTestIds.run,
		nodeKey: "review",
		kind: "Pause",
		status: "WaitingForApproval",
		attempt: 1,
		failureClass: "None",
		pendingDecisionKind: "Approve",
		error: null,
		input: {
			run: { id: graphWorkflowTestIds.run, definitionVersion: 1 },
			input: { request: "Summarise the release notes." },
			upstream: { analyze: { text: "Needs a human look.", json: { requiresReview: true } } },
		},
		output: null,
		invocationId: null,
		startedAtUtc: 1_700_000_220_000,
		completedAtUtc: null,
		updatedAtUtc: 1_700_000_220_000,
		...overrides,
	};
}

/** The Agent node's detail, with the envelope every node-run output carries: `{ status, attempt, branch?, output }`. */
export function agentNodeRunDetail(overrides: Partial<GraphWorkflowNodeRunResponse> = {}): GraphWorkflowNodeRunResponse {
	return {
		id: "nr-analyze",
		runId: graphWorkflowTestIds.run,
		nodeKey: "analyze",
		kind: "Agent",
		status: "Succeeded",
		attempt: 1,
		failureClass: "None",
		pendingDecisionKind: null,
		error: null,
		input: { run: { id: graphWorkflowTestIds.run }, input: { request: "Summarise the release notes." }, upstream: {} },
		output: {
			status: "succeeded",
			attempt: 1,
			output: { text: "The release removes two endpoints, so a human should confirm.", json: { requiresReview: true } },
		},
		invocationId: "inv-analyze",
		startedAtUtc: 1_700_000_200_000,
		completedAtUtc: 1_700_000_210_000,
		updatedAtUtc: 1_700_000_210_000,
		...overrides,
	};
}

export function graphWorkflowRunEvent(overrides: Partial<GraphWorkflowRunEventResponse> = {}): GraphWorkflowRunEventResponse {
	return {
		id: "ev-1",
		seq: 1,
		eventType: "run.created",
		nodeKey: null,
		detail: null,
		createdAtUtc: 1_700_000_200_000,
		...overrides,
	};
}

/** A trail covering the run above, including the two S2 event types and a `node.retried` with its detail document. */
export function graphWorkflowEvents(
	overrides: Partial<ListGraphWorkflowRunEventsResponse> = {},
): ListGraphWorkflowRunEventsResponse {
	return {
		events: [
			graphWorkflowRunEvent({ id: "ev-1", seq: 1, eventType: "run.created" }),
			graphWorkflowRunEvent({ id: "ev-2", seq: 2, eventType: "run.started" }),
			graphWorkflowRunEvent({ id: "ev-3", seq: 3, eventType: "node.completed", nodeKey: "analyze" }),
			graphWorkflowRunEvent({ id: "ev-4", seq: 4, eventType: "node.started", nodeKey: "lookup" }),
			graphWorkflowRunEvent({
				id: "ev-5",
				seq: 5,
				eventType: "node.retried",
				nodeKey: "lookup",
				detail: { failureClass: "Timeout", attempt: 1, reason: "The tool did not answer within the node timeout." },
			}),
			graphWorkflowRunEvent({ id: "ev-6", seq: 6, eventType: "node.failed", nodeKey: "lookup" }),
			graphWorkflowRunEvent({ id: "ev-7", seq: 7, eventType: "gate.requested", nodeKey: "review" }),
			graphWorkflowRunEvent({ id: "ev-8", seq: 8, eventType: "run.waiting" }),
		],
		lastSeq: 8,
		replayTruncated: false,
		...overrides,
	};
}

/**
 * The eight tools the node actually offers, already D6-filtered server-side — never re-filter this list. `GetCurrentTime`
 * and `Calculate` keep their PascalCase method names (`AIFunctionFactory` derives the name from the method), the six
 * others are snake_case. `parameterSchema` is raw JSON-schema TEXT that the argument form parses.
 */
export function graphWorkflowTools(overrides: Partial<ListGraphWorkflowToolsResponse> = {}): ListGraphWorkflowToolsResponse {
	return {
		tools: [
			{
				name: "GetCurrentTime",
				description: "The current time on this node.",
				parameterSchema: '{"type":"object","properties":{}}',
			},
			{
				name: "Calculate",
				description: "Evaluates an arithmetic expression.",
				parameterSchema: '{"type":"object","properties":{"expression":{"type":"string"}},"required":["expression"]}',
			},
			{
				name: "list_files",
				description: "Lists the files under a registered source path.",
				parameterSchema: '{"type":"object","properties":{"path":{"type":"string"}}}',
			},
			{
				name: "read_file",
				description: "Reads a text file under a registered source path.",
				parameterSchema: '{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}',
			},
			{
				name: "search_text",
				description: "Searches the registered sources for a literal string.",
				parameterSchema: '{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}',
			},
			{
				name: "search_knowledge_base",
				description: "Semantic search over the knowledge base.",
				parameterSchema: '{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}',
			},
			{
				name: "read_document",
				description: "Reads one knowledge-base document.",
				parameterSchema: '{"type":"object","properties":{"documentId":{"type":"string"}},"required":["documentId"]}',
			},
			{
				name: "read_surrounding_chunks",
				description: "Reads the chunks around a knowledge-base hit.",
				parameterSchema: '{"type":"object","properties":{"chunkId":{"type":"string"}},"required":["chunkId"]}',
			},
		],
		...overrides,
	};
}
