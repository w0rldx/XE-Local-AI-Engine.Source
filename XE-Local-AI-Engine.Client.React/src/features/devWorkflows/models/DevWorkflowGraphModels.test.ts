// The wire → canvas mapping, which is where the two id spaces meet: the pinned graph's edges name NODE KEYS, the
// node-run rows carry ids, and everything downstream (selection, the layout, the anchors) works in ids.

import { describe, expect, it } from "vitest";

import {
	DEV_WORKFLOW_MAX_RENDERED_NODES,
	type DevWorkflowCanvasNodeData,
	devWorkflowGraphStructuralKey,
	toDevWorkflowCanvasGraph,
	toDevWorkflowDefinitionCanvasGraph,
} from "@/features/devWorkflows/models/DevWorkflowGraphModels";
import type { DevWorkflowRunResponse } from "@/features/devWorkflows/models/DevWorkflowModels";
import { devWorkflowNodeRunSummary, devWorkflowRun } from "@/features/devWorkflows/test/DevWorkflowFixtures";

/** research → plan, as the runtime actually serves it: edges by node key, node-runs by id. */
function chainRun(overrides: Partial<DevWorkflowRunResponse> = {}): DevWorkflowRunResponse {
	return devWorkflowRun({
		graph: { schemaVersion: 1, nodes: [], edges: [{ from: "research", to: "plan" }] },
		nodes: [
			devWorkflowNodeRunSummary({ id: "node-research", nodeKey: "research", label: "Research", sequence: 1 }),
			devWorkflowNodeRunSummary({
				id: "node-plan",
				nodeKey: "plan",
				nodeType: "HumanGate",
				label: "Approve the plan",
				status: "Pending",
				sequence: 2,
			}),
		],
		...overrides,
	});
}

function dataOf(graph: ReturnType<typeof toDevWorkflowCanvasGraph>, id: string): DevWorkflowCanvasNodeData {
	const node = graph.nodes.find((candidate) => candidate.id === id);
	if (!node) {
		throw new Error(`no canvas node for ${id}`);
	}
	return node.data as DevWorkflowCanvasNodeData;
}

describe("toDevWorkflowCanvasGraph", () => {
	it("gives every node-run a card keyed by its node-run id, carrying its run state", () => {
		const graph = toDevWorkflowCanvasGraph(
			chainRun({
				nodes: [
					devWorkflowNodeRunSummary({
						id: "node-research",
						nodeKey: "research",
						status: "Blocked",
						attempt: 3,
						isMaterialized: true,
						materializedFromNodeKey: "decompose",
						materializationIndex: 2,
						hasStaleInputs: true,
					}),
				],
			}),
		);

		const card = graph.nodes.find((node) => node.id === "node-research");
		expect(card?.type).toBe("Agent");
		expect([card?.draggable, card?.connectable, card?.deletable]).toEqual([false, false, false]);
		expect(dataOf(graph, "node-research")).toMatchObject({
			label: "Research",
			status: "Blocked",
			attempt: 3,
			maxAttempts: 3,
			isMaterialized: true,
			materializedFromNodeKey: "decompose",
			materializationIndex: 2,
			hasStaleInputs: true,
			agentDisplayName: "Researcher",
			modelLabel: "qwen3-30b",
		});
	});

	it("reads each materialization's group size off the node-run row rather than counting the feed", () => {
		// The runtime counts the CHILDREN of a decomposition. The client cannot: C2 clones a template SUBTREE whole, so
		// two children of a two-node template are four rows sharing one origin, and counting rows told every card of
		// that group it was "… of 4". Both groups below are two children over four rows, which is the case that broke.
		const graph = toDevWorkflowCanvasGraph(
			chainRun({
				nodes: [
					devWorkflowNodeRunSummary({ id: "node-plan", nodeKey: "plan" }),
					...[1, 2].flatMap((child) =>
						["implement", "validate"].map((step) =>
							devWorkflowNodeRunSummary({
								id: `node-${step}-${child}`,
								nodeKey: `${step}#child-${child}`,
								isMaterialized: true,
								materializedFromNodeKey: "decompose",
								materializationGroupId: "group-decompose",
								materializationIndex: child,
								materializationCount: 2,
							}),
						),
					),
				],
			}),
		);

		expect(
			[1, 2]
				.flatMap((child) => ["implement", "validate"].map((step) => dataOf(graph, `node-${step}-${child}`)))
				.map((data) => [data.materializationIndex, data.materializationCount]),
		).toEqual([
			[1, 2],
			[1, 2],
			[2, 2],
			[2, 2],
		]);
		// A node the definition named is not part of anyone's group, so it carries no count at all.
		expect(dataOf(graph, "node-plan").materializationCount).toBeUndefined();
	});

	it("reads a RUN's apply node off the pinned graph, since a node-run row carries no toolMode", () => {
		const graph = toDevWorkflowCanvasGraph(
			devWorkflowRun({
				graph: {
					schemaVersion: 1,
					nodes: [
						{ nodeKey: "integrate", nodeType: "Tool", label: "Integrate", toolMode: "Apply" },
						{ nodeKey: "validate", nodeType: "Tool", label: "Validate" },
					],
					edges: [{ from: "validate", to: "integrate" }],
				},
				nodes: [
					devWorkflowNodeRunSummary({ id: "n0", nodeKey: "integrate", nodeType: "Tool" }),
					devWorkflowNodeRunSummary({ id: "n1", nodeKey: "validate", nodeType: "Tool" }),
				],
			}),
		);

		expect([dataOf(graph, "n0").isApplyTool, dataOf(graph, "n1").isApplyTool]).toEqual([true, false]);
	});

	it("joins graph edges to node-runs by node key, so the drawn edge is between node-run ids", () => {
		const graph = toDevWorkflowCanvasGraph(chainRun());

		expect(graph.edges.map((edge) => [edge.source, edge.target])).toContainEqual(["node-research", "node-plan"]);
	});

	it("drops an edge whose endpoint has no node-run row, because there is nothing to draw it to", () => {
		const graph = toDevWorkflowCanvasGraph(
			chainRun({
				// A materialization template has no node-run row until Slice C materializes its children.
				graph: {
					schemaVersion: 1,
					nodes: [],
					edges: [
						{ from: "research", to: "plan" },
						{ from: "plan", to: "decompose-template" },
					],
				},
			}),
		);

		expect(graph.edges.some((edge) => edge.target === "decompose-template")).toBe(false);
		// And `plan` is NOT capped with an End anchor: it has a successor the canvas cannot draw yet, so it dangles
		// rather than claiming the run finishes there. Degree is asked of the DEFINITION's edges for exactly this.
		const anchors = graph.nodes.filter((node) => node.type === "anchor");
		expect(anchors.map((anchor) => anchor.data["anchor"])).toEqual(["start"]);
	});

	it("synthesises a start anchor at the entry and an end anchor at the terminal, neither of them selectable", () => {
		const graph = toDevWorkflowCanvasGraph(chainRun());

		const anchors = graph.nodes.filter((node) => node.type === "anchor");
		expect(anchors).toHaveLength(2);
		expect(anchors.every((anchor) => anchor.selectable === false)).toBe(true);
		// Ranked with the real nodes rather than offset from them: the start sits left of the entry, the end right of
		// the terminal, so an anchor cannot land on top of anything.
		const start = anchors.find((anchor) => anchor.data["anchor"] === "start");
		const end = anchors.find((anchor) => anchor.data["anchor"] === "end");
		const research = graph.nodes.find((node) => node.id === "node-research");
		const plan = graph.nodes.find((node) => node.id === "node-plan");
		expect(start?.position.x).toBeLessThan(research?.position.x ?? 0);
		expect(end?.position.x).toBeGreaterThan(plan?.position.x ?? 0);
	});

	it("gives a node that is both entry and terminal both anchors", () => {
		const graph = toDevWorkflowCanvasGraph(
			devWorkflowRun({ graph: { schemaVersion: 1, nodes: [], edges: [] }, nodes: [devWorkflowNodeRunSummary()] }),
		);

		expect(graph.nodes.filter((node) => node.type === "anchor")).toHaveLength(2);
	});

	it("keeps the structural key identical when only a status ticks, so the viewport is not re-framed", () => {
		const before = devWorkflowGraphStructuralKey(chainRun());
		const after = devWorkflowGraphStructuralKey(
			chainRun({
				nodes: [
					devWorkflowNodeRunSummary({ id: "node-research", nodeKey: "research", status: "Succeeded" }),
					devWorkflowNodeRunSummary({ id: "node-plan", nodeKey: "plan", status: "Running" }),
				],
			}),
		);

		expect(after).toBe(before);
	});

	it("keeps the structural key identical when the server returns the same nodes in a different order", () => {
		const forward = chainRun();
		const reversed = chainRun({ nodes: (forward.nodes ?? []).toReversed() });

		expect(devWorkflowGraphStructuralKey(reversed)).toBe(devWorkflowGraphStructuralKey(forward));
	});

	it("changes the structural key when a node is materialized into the run", () => {
		const before = devWorkflowGraphStructuralKey(chainRun());
		const after = devWorkflowGraphStructuralKey(
			chainRun({
				nodes: [
					devWorkflowNodeRunSummary({ id: "node-research", nodeKey: "research" }),
					devWorkflowNodeRunSummary({ id: "node-plan", nodeKey: "plan" }),
					devWorkflowNodeRunSummary({ id: "node-child", nodeKey: "task", isMaterialized: true }),
				],
			}),
		);

		expect(after).not.toBe(before);
	});

	it("refuses to draw a run past the render cap and says how many nodes it has", () => {
		const nodes = Array.from({ length: DEV_WORKFLOW_MAX_RENDERED_NODES + 1 }, (_, index) =>
			devWorkflowNodeRunSummary({ id: `node-${index}`, nodeKey: `key-${index}` }),
		);

		const overCap = toDevWorkflowCanvasGraph(devWorkflowRun({ nodes }));
		expect(overCap.isOverCap).toBe(true);
		expect(overCap.nodeRunCount).toBe(DEV_WORKFLOW_MAX_RENDERED_NODES + 1);
		expect(overCap.nodes).toEqual([]);

		// The cap matches the server's MaxNodeRunsPerRun exactly, so a run AT the bound still draws.
		expect(toDevWorkflowCanvasGraph(devWorkflowRun({ nodes: nodes.slice(1) })).isOverCap).toBe(false);
	});

	it("renders nothing rather than throwing when there is no run yet", () => {
		const graph = toDevWorkflowCanvasGraph(undefined);

		expect(graph.nodes).toEqual([]);
		expect(graph.edges).toEqual([]);
	});
});

describe("toDevWorkflowDefinitionCanvasGraph", () => {
	const definitionGraph = {
		schemaVersion: 1,
		nodes: [
			{ nodeKey: "research", nodeType: "Agent", label: "Research" },
			{ nodeKey: "plan", nodeType: "HumanGate", label: "Approve the plan" },
		],
		edges: [{ from: "research", to: "plan" }],
	};

	it("draws a definition with NO status, because nothing has run it", () => {
		const graph = toDevWorkflowDefinitionCanvasGraph(definitionGraph);

		// A definition node painted `Pending` would claim it is materialized and waiting on a dependency, which is a
		// run's state. The card reads the absence and renders no badge at all.
		expect(dataOf(graph, "research").status).toBeUndefined();
		expect(dataOf(graph, "plan").status).toBeUndefined();
		expect(dataOf(graph, "plan").nodeType).toBe("HumanGate");
	});

	it("keys nodes and edges by node key, since a definition has no rows to join against", () => {
		const graph = toDevWorkflowDefinitionCanvasGraph(definitionGraph);

		expect(graph.nodes.filter((node) => node.type !== "anchor").map((node) => node.id)).toEqual(["research", "plan"]);
		expect(graph.edges.some((edge) => edge.source === "research" && edge.target === "plan")).toBe(true);
		// Y6's anchors are computed the same way for both sources: one Start, one End on a linear chain.
		expect(graph.nodes.filter((node) => node.type === "anchor")).toHaveLength(2);
	});

	it("reads an Apply-mode Tool node off the definition, whatever casing the document used", () => {
		// The seeded template's `integrate` node is the only thing on this system that can land a patch, and on a DRAFT
		// preview nothing has run — so the graph node's `toolMode` is the only place that fact exists.
		const graph = toDevWorkflowDefinitionCanvasGraph({
			schemaVersion: 1,
			nodes: [
				{ nodeKey: "integrate", nodeType: "Tool", label: "Integrate", toolMode: "Apply" },
				{ nodeKey: "shouty", nodeType: "Tool", label: "Shouty", toolMode: "APPLY" },
				{ nodeKey: "validate", nodeType: "Tool", label: "Validate", toolMode: "Validate" },
				// A node authored before `toolMode` existed stores none at all; absent is Validate, as it is server-side.
				{ nodeKey: "legacy", nodeType: "Tool", label: "Legacy" },
			],
			edges: [{ from: "integrate", to: "validate" }],
		});

		expect(["integrate", "shouty", "validate", "legacy"].map((key) => dataOf(graph, key).isApplyTool)).toEqual([
			true,
			true,
			false,
			false,
		]);
	});

	it("renders nothing rather than throwing when no definition has been picked", () => {
		expect(toDevWorkflowDefinitionCanvasGraph(undefined).nodes).toEqual([]);
	});
});
