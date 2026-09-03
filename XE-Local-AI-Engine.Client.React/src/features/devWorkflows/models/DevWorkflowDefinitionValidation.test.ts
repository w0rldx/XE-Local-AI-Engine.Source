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

/** `feature-development-v1`'s shape: a template subtree hanging off `decompose` and handed back at `join`. */
function templateGraph(overrides: Partial<DevWorkflowGraph> = {}): DevWorkflowGraph {
	return {
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
		...overrides,
	};
}

/** `feature-development-v1` as the seeder ships it — the graph every invariant here has to accept unchanged. */
function shippedTemplate(): DevWorkflowGraph {
	return {
		schemaVersion: 1,
		nodes: [
			node("research"),
			node("plan"),
			node("planapproval", { nodeType: "HumanGate" }),
			node("decompose", {
				materialization: { templateNodeKey: "implement", artifactKind: "TaskPackage", joinNodeKey: "join", maxChildren: 10 },
			}),
			node("implement", { nodeType: "DevTask", isTemplate: true }),
			node("validate", { nodeType: "Tool", retryTarget: "implement", isTemplate: true }),
			node("join", { nodeType: "Join" }),
			node("verify"),
			node("integrationapproval", { nodeType: "HumanGate" }),
			node("integrate", { nodeType: "Tool", toolMode: "Apply" }),
			node("fullvalidate", { nodeType: "Tool", retryTarget: "verify" }),
		],
		edges: [
			{ from: "research", to: "plan" },
			{ from: "plan", to: "planapproval" },
			{ from: "planapproval", to: "decompose", condition: { path: "decision", op: "eq", value: "Approve" } },
			{ from: "decompose", to: "join" },
			{ from: "implement", to: "validate" },
			{ from: "validate", to: "join" },
			{ from: "join", to: "verify" },
			{ from: "planapproval", to: "verify", condition: { path: "decision", op: "eq", value: "Approve" } },
			{ from: "verify", to: "integrationapproval" },
			{ from: "integrationapproval", to: "integrate", condition: { path: "decision", op: "eq", value: "Approve" } },
			{ from: "integrate", to: "fullvalidate" },
		],
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
		// `implement` hangs off nothing and is reached by nothing from outside its own subtree. Judged as an ordinary
		// node it reads as a second entry AND as unreachable, which is two refusals over a graph the server accepts.
		expect(validateDevWorkflowGraph(templateGraph())).toEqual([]);
	});

	it("takes the whole template SUBTREE as edited, so a node added inside one is not an orphan", () => {
		// The wire's `isTemplate` is the server's verdict over the document as LOADED. A node the operator has just
		// added inside the subtree carries no flag, and trusting the flag blocked a save the server accepts.
		expect(
			validateDevWorkflowGraph(
				templateGraph({
					nodes: [
						node("decompose", { materialization: { templateNodeKey: "implement", joinNodeKey: "join", maxChildren: 5 } }),
						node("implement", { nodeType: "DevTask", isTemplate: true }),
						node("validate", { nodeType: "Tool" }),
						node("join", { nodeType: "Join" }),
					],
					edges: [
						{ from: "decompose", to: "join" },
						{ from: "implement", to: "validate" },
						{ from: "validate", to: "join" },
					],
				}),
			),
		).toEqual([]);
	});

	it("stops the subtree at the join, so the rest of the run is not swallowed into the template", () => {
		// Walking through the join would make `join` and `verify` templates too, and then a graph with a genuinely
		// unreachable tail would validate clean.
		expect(
			rules(
				templateGraph({
					nodes: [
						node("decompose", { materialization: { templateNodeKey: "implement", joinNodeKey: "join", maxChildren: 5 } }),
						node("implement", { nodeType: "DevTask", isTemplate: true }),
						node("join", { nodeType: "Join" }),
						node("verify"),
						node("stranded"),
					],
					edges: [
						{ from: "decompose", to: "join" },
						{ from: "implement", to: "join" },
						{ from: "join", to: "verify" },
						{ from: "stranded", to: "verify" },
					],
				}),
			),
		).toContain("orphan");
	});

	it("stops treating a node as a template once the materializing node is deleted", () => {
		// The stale `isTemplate: true` says otherwise. Believing it hid the second entry the server would refuse with
		// a 400 the form could not explain.
		expect(
			rules(
				templateGraph({
					nodes: [node("implement", { nodeType: "DevTask", isTemplate: true }), node("join", { nodeType: "Join" })],
					edges: [{ from: "implement", to: "join" }],
				}),
			),
		).toEqual([]);
		expect(
			rules(
				templateGraph({
					nodes: [node("start"), node("implement", { nodeType: "DevTask", isTemplate: true }), node("join", { nodeType: "Join" })],
					edges: [
						{ from: "start", to: "join" },
						{ from: "implement", to: "join" },
					],
				}),
			),
		).toContain("multipleEntries");
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

	it("refuses a gate out-edge no answer would take, and names the gate that owns it", () => {
		// "Approved" is the past participle. It validates, it never fires, and the branch behind it is dead — which is
		// exactly the mistake GRAPH-C4-1 exists to catch at authoring time.
		const dead = validateDevWorkflowGraph(
			chain({
				nodes: [node("research"), node("approval", { nodeType: "HumanGate" }), node("integrate")],
				edges: [
					{ from: "research", to: "approval" },
					{ from: "approval", to: "integrate", condition: { path: "decision", op: "eq", value: "Approved" } },
				],
			}),
		);
		expect(dead.map((issue) => issue.rule)).toContain("deadGateEdge");
		expect(dead.find((issue) => issue.rule === "deadGateEdge")?.subject).toBe("approval");

		// The same edge on the answer the gate can actually give is routing, not a fault.
		expect(
			validateDevWorkflowGraph(
				chain({
					nodes: [node("research"), node("approval", { nodeType: "HumanGate" }), node("integrate")],
					edges: [
						{ from: "research", to: "approval" },
						{ from: "approval", to: "integrate", condition: { path: "decision", op: "eq", value: "Approve" } },
					],
				}),
			),
		).toEqual([]);
	});

	it("names a stranded branch on the node that is stranded, not on the gate above it", () => {
		// Two chained gates: `beta` owns the dead edge, `alpha` is stranded because everything past beta is. The gate
		// that owns it is named first, or the operator is sent to fix the line that is fine.
		const issues = validateDevWorkflowGraph({
			schemaVersion: 1,
			nodes: [
				node("alpha", { nodeType: "HumanGate" }),
				node("beta", { nodeType: "HumanGate" }),
				node("done"),
			],
			edges: [
				{ from: "alpha", to: "beta", condition: { path: "decision", op: "eq", value: "Approve" } },
				{ from: "beta", to: "done", condition: { path: "decision", op: "eq", value: "Approved" } },
			],
		});
		expect(issues.map((issue) => issue.rule)).toContain("deadGateEdge");
		expect(issues.find((issue) => issue.rule === "deadGateEdge")?.subject).toBe("beta");
		expect(issues.map((issue) => issue.rule)).not.toContain("strandedBranch");
	});

	it("refuses a declared write a run can reach without anyone being asked, and takes the template's waiver for it", () => {
		const ungated = chain({
			nodes: [node("research"), node("release", { requiredCapabilities: { WriteExecute: "runs the release script" } })],
			edges: [{ from: "research", to: "release" }],
		});
		expect(rules(ungated)).toContain("ungatedWrite");
		expect(validateDevWorkflowGraph({ ...ungated, allowUngatedWrites: true })).toEqual([]);

		// A gate on the one path into it is the other answer, and it is the one the rule is actually asking for.
		expect(
			validateDevWorkflowGraph(
				chain({
					nodes: [
						node("approval", { nodeType: "HumanGate" }),
						node("release", { requiredCapabilities: { WriteExecute: "runs the release script" } }),
					],
					edges: [{ from: "approval", to: "release" }],
				}),
			),
		).toEqual([]);

		// A DevTask writes a worktree under its own data root, not the repository, so it is not what this rule is about.
		expect(rules(chain({ nodes: [node("research"), node("implement", { nodeType: "DevTask" })], edges: [{ from: "research", to: "implement" }] }))).not.toContain(
			"ungatedWrite",
		);
	});

	it("refuses an apply a run can reach with no validation having run", () => {
		const unvalidated = {
			schemaVersion: 1,
			nodes: [node("approval", { nodeType: "HumanGate" }), node("integrate", { nodeType: "Tool", toolMode: "Apply" })],
			edges: [{ from: "approval", to: "integrate", condition: { path: "decision", op: "eq", value: "Approve" } }],
		} satisfies DevWorkflowGraph;
		expect(rules(unvalidated)).toContain("applyWithoutValidation");

		// A Tool node with no mode IS a validation — absent reads as `Validate`, exactly as the parser reads it.
		expect(
			validateDevWorkflowGraph({
				...unvalidated,
				nodes: [node("validate", { nodeType: "Tool" }), ...unvalidated.nodes],
				edges: [{ from: "validate", to: "approval" }, ...unvalidated.edges],
			}),
		).toEqual([]);
	});

	it("accepts the shipped template, which is what keying the fixpoint on joinPolicy rather than node type buys", () => {
		// `verify` is an AGENT with two inbound edges. A `Combine` keyed on `NodeType === "Join"` would treat it as a
		// plain node, lose the property carried through the join, and refuse the one template that ships.
		expect(validateDevWorkflowGraph(shippedTemplate())).toEqual([]);
	});

	it("needs the property on EVERY branch of an Any join, because only one of them will have run", () => {
		// One branch validates, the other does not, and the run may take either — so the apply behind the Any join is
		// not assured. Under `All` the same shape passes: every branch completes, so one validation is enough.
		const branches = (joinPolicy: string): DevWorkflowGraph => ({
			schemaVersion: 1,
			nodes: [
				node("split"),
				node("validate", { nodeType: "Tool" }),
				node("other"),
				node("join", { nodeType: "Join", joinPolicy }),
				node("approval", { nodeType: "HumanGate" }),
				node("integrate", { nodeType: "Tool", toolMode: "Apply" }),
			],
			edges: [
				{ from: "split", to: "validate" },
				{ from: "split", to: "other" },
				{ from: "validate", to: "join" },
				{ from: "other", to: "join" },
				{ from: "join", to: "approval" },
				{ from: "approval", to: "integrate", condition: { path: "decision", op: "eq", value: "Approve" } },
			],
		});
		expect(rules(branches("Any"))).toContain("applyWithoutValidation");
		expect(validateDevWorkflowGraph(branches("All"))).toEqual([]);
	});

	it("counts the entry node's own property, because a template may START with the gate or with the validation", () => {
		// Both shapes are valid and both were 400s under a fixpoint initialised false on the entry.
		expect(
			validateDevWorkflowGraph({
				schemaVersion: 1,
				nodes: [
					node("approval", { nodeType: "HumanGate" }),
					node("release", { requiredCapabilities: { WriteExecute: "runs the release script" } }),
				],
				edges: [{ from: "approval", to: "release" }],
			}),
		).toEqual([]);
		expect(
			validateDevWorkflowGraph({
				schemaVersion: 1,
				nodes: [
					node("validate", { nodeType: "Tool" }),
					node("approval", { nodeType: "HumanGate" }),
					node("integrate", { nodeType: "Tool", toolMode: "Apply" }),
				],
				edges: [
					{ from: "validate", to: "approval" },
					{ from: "approval", to: "integrate", condition: { path: "decision", op: "eq", value: "Approve" } },
				],
			}),
		).toEqual([]);
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
