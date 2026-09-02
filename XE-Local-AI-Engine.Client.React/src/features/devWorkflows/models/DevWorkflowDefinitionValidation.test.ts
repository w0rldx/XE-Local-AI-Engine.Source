// The save gate of the definition editor, rule by rule. Each one mirrors a rule the SERVER's graph parser enforces,
// so the test that matters for every one of them is the same pair: the shape the server rejects is reported here, and
// the shape it accepts produces nothing.

import { describe, expect, it } from "vitest";

import { validateDevWorkflowGraph } from "@/features/devWorkflows/models/DevWorkflowDefinitionValidation";
import type { DevWorkflowGraph, DevWorkflowGraphNode } from "@/features/devWorkflows/models/DevWorkflowModels";

function node(nodeKey: string, overrides: Partial<DevWorkflowGraphNode> = {}): DevWorkflowGraphNode {
	return { nodeKey, nodeType: "Agent", label: nodeKey, ...overrides };
}

/** research → plan → approval: the seeded linear template, and the shape every negative case is a mutation of. */
function chain(overrides: Partial<DevWorkflowGraph> = {}): DevWorkflowGraph {
	return {
		schemaVersion: 1,
		nodes: [node("research"), node("plan"), node("approval")],
		edges: [
			{ from: "research", to: "plan" },
			{ from: "plan", to: "approval" },
		],
		...overrides,
	};
}

function rules(graph: DevWorkflowGraph | undefined): readonly string[] {
	return validateDevWorkflowGraph(graph).map((issue) => issue.rule);
}

describe("validateDevWorkflowGraph", () => {
	it("passes the seeded linear template, and answers nothing for no graph at all", () => {
		expect(validateDevWorkflowGraph(chain())).toEqual([]);
		expect(validateDevWorkflowGraph(undefined)).toEqual([]);
	});

	it("rejects a node with no key, and two nodes sharing one", () => {
		expect(rules(chain({ nodes: [node(""), node("plan")] }))).toContain("missingNodeKey");

		const duplicate = validateDevWorkflowGraph(chain({ nodes: [node("research"), node("research"), node("plan")] }));
		expect(duplicate.map((issue) => issue.rule)).toContain("duplicateNodeKey");
		expect(duplicate.find((issue) => issue.rule === "duplicateNodeKey")?.subject).toBe("research");
	});

	it("rejects an edge whose endpoint is not a declared node", () => {
		const issues = validateDevWorkflowGraph(chain({ edges: [{ from: "research", to: "nowhere" }] }));

		expect(issues.map((issue) => issue.rule)).toContain("unknownEdgeEndpoint");
		expect(issues.find((issue) => issue.rule === "unknownEdgeEndpoint")?.subject).toBe("research → nowhere");
	});

	it("rejects the same edge declared twice", () => {
		expect(
			rules(
				chain({
					edges: [
						{ from: "research", to: "plan" },
						{ from: "research", to: "plan" },
						{ from: "plan", to: "approval" },
					],
				}),
			),
		).toContain("duplicateEdge");
	});

	it("insists on exactly one entry node, naming the extra one", () => {
		// Two roots: `plan` is reached, `research` and `stray` are not.
		const issues = validateDevWorkflowGraph(
			chain({ nodes: [node("research"), node("plan"), node("approval"), node("stray")] }),
		);

		expect(issues.map((issue) => issue.rule)).toContain("multipleEntries");
		expect(issues.find((issue) => issue.rule === "multipleEntries")?.subject).toBe("stray");
	});

	it("reports a graph where every node has an inbound edge as having no entry at all", () => {
		expect(
			rules({
				schemaVersion: 1,
				nodes: [node("a"), node("b")],
				edges: [
					{ from: "a", to: "b" },
					{ from: "b", to: "a" },
				],
			}),
		).toContain("noEntry");
	});

	it("rejects a cycle, which is the shape an author reaches for when they mean a fix loop", () => {
		expect(
			rules(
				chain({
					edges: [
						{ from: "research", to: "plan" },
						{ from: "plan", to: "approval" },
						{ from: "approval", to: "plan" },
					],
				}),
			),
		).toContain("cycle");
	});

	it("accepts a diamond, which is a shared successor and not a loop", () => {
		expect(
			validateDevWorkflowGraph({
				schemaVersion: 1,
				nodes: [node("split"), node("left"), node("right"), node("join", { joinPolicy: "All" })],
				edges: [
					{ from: "split", to: "left" },
					{ from: "split", to: "right" },
					{ from: "left", to: "join" },
					{ from: "right", to: "join" },
				],
			}),
		).toEqual([]);
	});

	it("reports a node nothing reaches, because it would never run", () => {
		// A detached pair. It is reported twice over, and honestly so: `stray` is a second entry AND neither node is
		// reachable from the one entry the workflow actually starts at.
		const issues = validateDevWorkflowGraph({
			schemaVersion: 1,
			nodes: [node("research"), node("plan"), node("stray"), node("strayNext")],
			edges: [
				{ from: "research", to: "plan" },
				{ from: "stray", to: "strayNext" },
			],
		});

		expect(issues.filter((issue) => issue.rule === "orphan").map((issue) => issue.subject)).toEqual(["stray", "strayNext"]);
	});

	it("does not call a materialization template an entry or an orphan — it has neither by design", () => {
		// `implement` hangs off nothing and is reached by nothing from outside its own subtree. Without `isTemplate`
		// it reads as a second entry AND as unreachable, which is two refusals over a graph the server accepts.
		expect(
			validateDevWorkflowGraph({
				schemaVersion: 1,
				nodes: [
					node("decompose", { materialization: { templateNodeKey: "implement", joinNodeKey: "join", maxChildren: 5 } }),
					node("implement", { nodeType: "DevTask", isTemplate: true }),
					node("join", { nodeType: "Join" }),
				],
				edges: [
					{ from: "decompose", to: "join" },
					{ from: "implement", to: "join" },
				],
			}),
		).toEqual([]);
	});

	it("rejects a retry target this template does not declare", () => {
		expect(rules(chain({ nodes: [node("research"), node("plan", { retryTarget: "nowhere" }), node("approval")] }))).toContain(
			"unknownRetryTarget",
		);
	});

	it("insists a retry target is UPSTREAM, because a fix loop re-runs the work this node depended on", () => {
		// `approval` retrying to `plan` is legal: plan reaches approval. The reverse is not.
		expect(
			validateDevWorkflowGraph(chain({ nodes: [node("research"), node("plan"), node("approval", { retryTarget: "plan" })] })),
		).toEqual([]);
		expect(
			rules(chain({ nodes: [node("research"), node("plan", { retryTarget: "approval" }), node("approval")] })),
		).toContain("retryTargetNotAncestor");
	});

	it("insists an Any join has at least two inbound edges, since one is the same as All", () => {
		expect(
			rules(chain({ nodes: [node("research"), node("plan", { joinPolicy: "Any" }), node("approval")] })),
		).toContain("joinAnyNeedsTwoInbound");

		// Case-insensitive, the way the server's own `Enum.TryParse(..., ignoreCase: true)` reads it.
		expect(
			validateDevWorkflowGraph({
				schemaVersion: 1,
				nodes: [node("split"), node("left"), node("right"), node("join", { joinPolicy: "any" })],
				edges: [
					{ from: "split", to: "left" },
					{ from: "split", to: "right" },
					{ from: "left", to: "join" },
					{ from: "right", to: "join" },
				],
			}),
		).toEqual([]);
	});
});
