// The run view's canvas. Three things have to hold or the view lies about the run: the shape comes from the pinned
// graph and only while the hashes agree, a status tick never re-frames the viewport, and past the cap nothing is drawn
// at all rather than a canvas that takes a minute to lay out.

import { describe, expect, it } from "vitest";

import type {
	GraphWorkflowGraph,
	GraphWorkflowNodeRunSummaryResponse,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { toGraphWorkflowRunCanvas } from "@/features/graphWorkflows/models/GraphWorkflowRunGraph";
import {
	eightNodeGraph,
	graphWorkflowRun,
	graphWorkflowRunSummary,
	graphWorkflowTestGraphHash,
	graphWorkflowTestGuid,
	makeNodeRun,
} from "@/features/graphWorkflows/test/GraphWorkflowFixtures";

const run = graphWorkflowRun();
const nodeRuns = run.nodeRuns ?? [];
const matchingDefinition = { graph: eightNodeGraph, graphHash: graphWorkflowTestGraphHash };

function canvasFor(
	rows: readonly GraphWorkflowNodeRunSummaryResponse[] = nodeRuns,
	definitionGraph: { graph: GraphWorkflowGraph | undefined; graphHash?: string | null } | undefined = matchingDefinition,
) {
	return toGraphWorkflowRunCanvas({ run: run.run, nodeRuns: rows, definitionGraph });
}

describe("toGraphWorkflowRunCanvas with a matching graph hash", () => {
	it("draws the definition's nodes and edges at the positions they were authored at", () => {
		const canvas = canvasFor();

		expect(canvas.graphMismatch).toBe(false);
		expect(canvas.nodes).toHaveLength(8);
		expect(canvas.edges).toHaveLength(9);
		expect(canvas.nodeCount).toBe(8);
		expect(canvas.isOverCap).toBe(false);
		expect(canvas.nodes.find((node) => node.id === "review")?.position).toEqual({ x: -120, y: 360 });
	});

	it("attaches each node run's state to its node, and nothing to a node that has no row", () => {
		const canvas = canvasFor(nodeRuns.filter((row) => row.nodeKey !== "done"));

		expect(canvas.nodes.find((node) => node.id === "lookup")?.data.runState).toEqual({
			status: "Failed",
			attempt: 3,
			failureClass: "AttemptsExhausted",
		});
		expect(canvas.nodes.find((node) => node.id === "review")?.data.runState?.pendingDecisionKind).toBe("Approve");
		expect(canvas.nodes.find((node) => node.id === "start")?.data.runState?.status).toBe("Succeeded");
		expect(canvas.nodes.find((node) => node.id === "done")?.data.runState).toBeUndefined();
	});

	it("renders read-only: nothing is draggable, connectable or deletable", () => {
		const canvas = canvasFor();

		expect(canvas.nodes.every((node) => node.draggable === false)).toBe(true);
		expect(canvas.nodes.every((node) => node.connectable === false)).toBe(true);
		expect(canvas.nodes.every((node) => node.deletable === false)).toBe(true);
		expect(canvas.edges.every((edge) => edge.selectable === false && edge.deletable === false)).toBe(true);
	});

	it("keeps the structural key stable across a status tick, so the viewport is never re-framed", () => {
		const before = canvasFor();
		const ticked = canvasFor(
			nodeRuns.map((row) => ({ ...row, status: "Running", attempt: (row.attempt ?? 1) + 1, updatedAtUtc: 1 })),
		);

		expect(ticked.structuralKey).toBe(before.structuralKey);
		// The states themselves did change — otherwise this test would pass on a canvas that ignored the rows.
		expect(ticked.nodes.find((node) => node.id === "start")?.data.runState?.status).toBe("Running");
	});

	it("draws nothing past the rendered-node cap and reports the real count", () => {
		const crowded: GraphWorkflowGraph = {
			schemaVersion: 1,
			nodes: Array.from({ length: 201 }, (_, index) => ({
				key: `agent-${index}`,
				kind: "Agent",
				position: { x: 0, y: index },
				config: {},
			})),
			edges: [],
		};

		const canvas = canvasFor(nodeRuns, { graph: crowded, graphHash: graphWorkflowTestGraphHash });

		expect(canvas.isOverCap).toBe(true);
		expect(canvas.nodes).toEqual([]);
		expect(canvas.edges).toEqual([]);
		expect(canvas.nodeCount).toBe(201);
		expect(canvas.structuralKey).not.toBe("");
	});
});

describe("toGraphWorkflowRunCanvas when the graphs disagree", () => {
	it("falls back to nodes only, laid out, and says the graph does not match", () => {
		const canvas = canvasFor(nodeRuns, { graph: eightNodeGraph, graphHash: "sha256:someone-saved-since" });

		expect(canvas.graphMismatch).toBe(true);
		expect(canvas.edges).toEqual([]);
		expect(canvas.nodes).toHaveLength(8);
		// Laid out, not read off the definition: with no edges every node is in rank 0 and stacks in key order.
		expect(canvas.nodes.map((node) => node.position.x)).toEqual(Array.from({ length: 8 }, () => 0));
		expect(new Set(canvas.nodes.map((node) => node.position.y)).size).toBe(8);
	});

	it("still renders a node run whose key is in no definition — the rows are the run", () => {
		const canvas = canvasFor(
			[...nodeRuns, makeNodeRun({ id: graphWorkflowTestGuid(99), nodeKey: "ghost", kind: "Agent", status: "Succeeded" })],
			{ graph: eightNodeGraph, graphHash: "sha256:someone-saved-since" },
		);

		expect(canvas.nodes.map((node) => node.id)).toContain("ghost");
		expect(canvas.nodes.find((node) => node.id === "ghost")?.data.runState?.status).toBe("Succeeded");
		// The card still knows which kind it is, so the run view draws the right shape for it.
		expect(canvas.nodes.find((node) => node.id === "ghost")?.type).toBe("agent");
	});

	it("does not claim a mismatch while the definition has not loaded", () => {
		const canvas = toGraphWorkflowRunCanvas({ run: run.run, nodeRuns });

		expect(canvas.graphMismatch).toBe(false);
		expect(canvas.nodes).toHaveLength(8);
		expect(canvas.edges).toEqual([]);
	});

	it("does not claim a match when the run carries no hash at all", () => {
		const canvas = toGraphWorkflowRunCanvas({
			run: graphWorkflowRunSummary({ graphHash: undefined }),
			nodeRuns,
			definitionGraph: { graph: eightNodeGraph, graphHash: undefined },
		});

		expect(canvas.graphMismatch).toBe(true);
		expect(canvas.edges).toEqual([]);
	});

	it("draws nothing past the cap on the nodes-only path either", () => {
		const many = Array.from({ length: 201 }, (_, index) =>
			makeNodeRun({ id: graphWorkflowTestGuid(index), nodeKey: `agent-${index}`, kind: "Agent" }),
		);

		const canvas = toGraphWorkflowRunCanvas({ run: run.run, nodeRuns: many });

		expect(canvas.isOverCap).toBe(true);
		expect(canvas.nodes).toEqual([]);
		expect(canvas.nodeCount).toBe(201);
	});

	it("has no run and no rows at all without throwing", () => {
		const canvas = toGraphWorkflowRunCanvas({ run: undefined, nodeRuns: [] });

		expect(canvas.nodes).toEqual([]);
		expect(canvas.nodeCount).toBe(0);
		expect(canvas.graphMismatch).toBe(false);
	});
});
