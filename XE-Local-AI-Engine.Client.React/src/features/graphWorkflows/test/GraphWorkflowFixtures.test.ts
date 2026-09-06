// The fixtures are shared across the feature's suite, so they get their own check: a fixture that drifts off the wire
// vocabulary turns every test built on it into a test of the client agreeing with itself.

import { describe, expect, it } from "vitest";

import {
	zXeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowDefinitionResponse as zDefinition,
	zXeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowDefinitionSummaryResponse as zDefinitionSummary,
	zXeLocalAiEngineClientEndpointsGraphWorkflowsV1ListGraphWorkflowRunEventsResponse as zEvents,
	zXeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowNodeRunResponse as zNodeRun,
	zXeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowRunResponse as zRun,
	zXeLocalAiEngineClientEndpointsGraphWorkflowsV1ListGraphWorkflowToolsResponse as zTools,
} from "@/core/api/generated/zod.gen";
import {
	asGraphWorkflowEventType,
	GRAPH_WORKFLOW_KEY_PATTERN,
	graphWorkflowNodeKinds,
	graphWorkflowNodeRunStatuses,
	graphWorkflowRunStatuses,
	normalizeGraphWorkflowConditionOperator,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import {
	agentNodeRunDetail,
	eightNodeGraph,
	graphWorkflowDefinition,
	graphWorkflowDefinitionSummary,
	graphWorkflowEvents,
	graphWorkflowRun,
	graphWorkflowRunEvent,
	graphWorkflowRunSummary,
	graphWorkflowTestGraphHash,
	graphWorkflowTestIds,
	graphWorkflowTools,
	makeNodeRun,
	pendingPauseNodeRun,
} from "@/features/graphWorkflows/test/GraphWorkflowFixtures";

describe("eightNodeGraph", () => {
	it("is the brief's eight-node graph with unique, well-formed keys in one namespace", () => {
		const nodeKeys = (eightNodeGraph.nodes ?? []).map((node) => node.key ?? "");
		const edgeKeys = (eightNodeGraph.edges ?? []).map((edge) => edge.key ?? "");

		expect(nodeKeys).toEqual(["start", "analyze", "check", "review", "lookup", "fanout", "merge", "done"]);
		expect(edgeKeys).toHaveLength(9);
		expect(new Set([...nodeKeys, ...edgeKeys]).size).toBe(17);
		for (const key of [...nodeKeys, ...edgeKeys]) {
			expect(GRAPH_WORKFLOW_KEY_PATTERN.test(key), key).toBe(true);
		}
	});

	it("uses only real node kinds and gives every node a position", () => {
		for (const node of eightNodeGraph.nodes ?? []) {
			expect(graphWorkflowNodeKinds).toContain(node.kind);
			expect(node.position).toBeDefined();
		}
	});

	it("wires every edge between two nodes that exist", () => {
		const nodeKeys = new Set((eightNodeGraph.nodes ?? []).map((node) => node.key));
		for (const edge of eightNodeGraph.edges ?? []) {
			expect(nodeKeys, edge.key).toContain(edge.from);
			expect(nodeKeys, edge.key).toContain(edge.to);
		}
	});

	it("routes the Condition on two handles and the Pause on output.decision", () => {
		const conditionEdges = (eightNodeGraph.edges ?? []).filter((edge) => edge.from === "check");
		expect(conditionEdges.map((edge) => edge.sourceHandle)).toEqual(["true", "false"]);
		// No `path` on either: both inherit the Condition node's own `config.path`.
		expect(conditionEdges.every((edge) => edge.condition?.path === undefined)).toBe(true);

		// One out-edge per allowed decision, or the server refuses the Pause node at save time.
		const pauseEdges = (eightNodeGraph.edges ?? []).filter((edge) => edge.from === "review");
		expect(pauseEdges.map((edge) => edge.sourceHandle)).toEqual(["Approve", "Reject"]);
		expect(pauseEdges.map((edge) => edge.condition)).toEqual([
			{ path: "output.decision", op: "Eq", value: "Approve" },
			{ path: "output.decision", op: "Eq", value: "Reject" },
		]);
	});

	it("gives every reconverging node an Any join, so no branch waits on one that was never taken", () => {
		// Both nodes with more than one inbound edge here reconverge the Condition's two exclusive branches — `fanout`
		// via the Pause and the Tool, `done` via the merge and the Pause's Reject. Under the default `All` join each
		// would wait forever for the branch the run did not take.
		const reconverging = (eightNodeGraph.nodes ?? []).filter(
			(node) => (eightNodeGraph.edges ?? []).filter((edge) => edge.to === node.key).length > 1,
		);

		expect(reconverging.map((node) => node.key)).toEqual(["fanout", "done"]);
		for (const node of reconverging) {
			expect(node.joinPolicy, node.key).toBe("Any");
		}
	});

	it("writes every operator in its canonical PascalCase form", () => {
		for (const edge of eightNodeGraph.edges ?? []) {
			const op = edge.condition?.op;
			if (op !== undefined) {
				expect(normalizeGraphWorkflowConditionOperator(op)).toBe(op);
			}
		}
	});
});

describe("definition builders", () => {
	it("wrap the graph at version 1 and agree on the hash and node count", () => {
		const definition = graphWorkflowDefinition();
		const summary = graphWorkflowDefinitionSummary();

		expect(definition.graph).toBe(eightNodeGraph);
		expect(definition.version).toBe(1);
		expect(definition.nodeCount).toBe(eightNodeGraph.nodes?.length);
		expect(summary.graphHash).toBe(definition.graphHash);
		expect(summary.graphHash).toBe(graphWorkflowTestGraphHash);
	});

	it("takes overrides", () => {
		expect(graphWorkflowDefinition({ version: 4 }).version).toBe(4);
		expect(graphWorkflowDefinitionSummary({ name: "Other" }).name).toBe("Other");
	});
});

describe("run builders", () => {
	it("materializes one node run per node key of the pinned graph", () => {
		const run = graphWorkflowRun();
		const nodeKeys = (eightNodeGraph.nodes ?? []).map((node) => node.key);

		expect((run.nodeRuns ?? []).map((nodeRun) => nodeRun.nodeKey)).toEqual(nodeKeys);
		expect(run.run?.graphHash).toBe(graphWorkflowTestGraphHash);
		expect(graphWorkflowRunStatuses).toContain(run.run?.status);
	});

	it("carries a realistic mix, including a failed node inside a run that is still going", () => {
		const run = graphWorkflowRun();
		const byKey = new Map((run.nodeRuns ?? []).map((nodeRun) => [nodeRun.nodeKey, nodeRun]));

		expect(run.run?.status).toBe("Running");
		expect(byKey.get("review")?.status).toBe("WaitingForApproval");
		expect(byKey.get("review")?.pendingDecisionKind).toBe("Approve");
		expect(byKey.get("lookup")?.status).toBe("Failed");
		expect(byKey.get("lookup")?.failureClass).toBe("AttemptsExhausted");
		expect(byKey.get("merge")?.status).toBe("Skipped");
		for (const nodeRun of run.nodeRuns ?? []) {
			expect(graphWorkflowNodeRunStatuses, nodeRun.nodeKey).toContain(nodeRun.status);
		}
	});

	it("builds a summary and a single node run that take overrides", () => {
		expect(graphWorkflowRunSummary({ status: "Completed" }).status).toBe("Completed");
		expect(makeNodeRun({ nodeKey: "done", kind: "End" }).nodeKey).toBe("done");
		expect(makeNodeRun().id).toBe(graphWorkflowTestIds.nodeRun);
	});
});

describe("node-run detail builders", () => {
	it("gives the Pause node a pending decision and an input document but no output", () => {
		const nodeRun = pendingPauseNodeRun();

		expect(nodeRun.runId).toBe(graphWorkflowTestIds.run);
		expect(nodeRun.pendingDecisionKind).toBe("Approve");
		expect(nodeRun.output).toBeNull();
		expect(nodeRun.input).toHaveProperty("upstream");
	});

	it("gives the Agent node the run-output envelope with text and schema json", () => {
		const output = agentNodeRunDetail().output as { status: string; attempt: number; output: { text: string; json: unknown } };

		expect(output.status).toBe("succeeded");
		expect(output.attempt).toBe(1);
		expect(output.output.text).toContain("human");
		expect(output.output.json).toEqual({ requiresReview: true });
	});
});

describe("event builders", () => {
	it("returns a contiguous, untruncated trail of known event types", () => {
		const feed = graphWorkflowEvents();
		const events = feed.events ?? [];

		expect(events).toHaveLength(8);
		expect(feed.replayTruncated).toBe(false);
		expect(feed.lastSeq).toBe(events.at(-1)?.seq);
		for (const event of events) {
			expect(asGraphWorkflowEventType(event.eventType), event.eventType).toBeDefined();
		}
	});

	it("carries the retry detail document a node.retried event needs", () => {
		const retried = (graphWorkflowEvents().events ?? []).find((event) => event.eventType === "node.retried");

		expect(retried?.nodeKey).toBe("lookup");
		expect(retried?.detail).toEqual({ failureClass: "Timeout", attempt: 1, reason: expect.any(String) });
	});

	it("builds a single event that takes overrides", () => {
		expect(graphWorkflowRunEvent({ eventType: "gate.decided" }).eventType).toBe("gate.decided");
	});
});

describe("graphWorkflowTools", () => {
	it("lists the eight tools the node offers, with the two PascalCase names intact", () => {
		const names = (graphWorkflowTools().tools ?? []).map((tool) => tool.name);

		expect(names).toEqual([
			"GetCurrentTime",
			"Calculate",
			"list_files",
			"read_file",
			"search_text",
			"search_knowledge_base",
			"read_document",
			"read_surrounding_chunks",
		]);
	});

	it("carries parameterSchema as parseable JSON-schema TEXT, not an object", () => {
		const readFile = (graphWorkflowTools().tools ?? []).find((tool) => tool.name === "read_file");

		expect(typeof readFile?.parameterSchema).toBe("string");
		expect(JSON.parse(readFile?.parameterSchema ?? "")).toEqual({
			type: "object",
			properties: { path: { type: "string" } },
			required: ["path"],
		});
	});
});

// The generated RESPONSE validators are what `withResponseValidation` runs on every payload, so a fixture that does not
// satisfy them is a fixture no MSW test can serve. Cheaper than asserting field shapes by hand, and it moves with the
// client: id members are `z.guid()`, which is why none of these carry a friendly `nr-start`.
describe("every fixture satisfies its generated response validator", () => {
	it.each([
		["definition", zDefinition, graphWorkflowDefinition()],
		["definitionSummary", zDefinitionSummary, graphWorkflowDefinitionSummary()],
		["run", zRun, graphWorkflowRun()],
		["pendingPauseNodeRun", zNodeRun, pendingPauseNodeRun()],
		["agentNodeRunDetail", zNodeRun, agentNodeRunDetail()],
		["events", zEvents, graphWorkflowEvents()],
		["tools", zTools, graphWorkflowTools()],
	])("%s parses", (_name, schema, fixture) => {
		const result = schema.safeParse(fixture);

		expect(result.error?.issues ?? []).toEqual([]);
		expect(result.success).toBe(true);
	});
});
