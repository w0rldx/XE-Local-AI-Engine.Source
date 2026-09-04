// @vitest-environment jsdom

// The structural panel describes a node by what surrounds it, so every fact it renders is a join back to the run's
// OTHER rows and the pinned graph's edges. The one that has no row by design is a materialization template, and
// reading its row-less-ness as anything but "template" told an operator a node that could not run had satisfied a
// dependency.

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { DevWorkflowStructuralNodePanel } from "@/features/devWorkflows/components/DevWorkflowStructuralNodePanel";
import type { DevWorkflowGraph } from "@/features/devWorkflows/models/DevWorkflowModels";
import { devWorkflowNodeRunDetail, devWorkflowNodeRunSummary, devWorkflowRun } from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

/** `feature-development-v1`'s shape: a template subtree hanging off `decompose` and handed back at `join`. */
const graph: DevWorkflowGraph = {
	schemaVersion: 1,
	nodes: [
		{ nodeKey: "decompose", nodeType: "Agent", label: "Decompose" },
		{ nodeKey: "implement", nodeType: "DevTask", label: "Implement", isTemplate: true },
		{ nodeKey: "validate", nodeType: "Tool", label: "Validate", isTemplate: true },
		{ nodeKey: "join", nodeType: "Join", label: "Join" },
	],
	edges: [
		{ from: "decompose", to: "join" },
		{ from: "implement", to: "validate" },
		{ from: "validate", to: "join" },
	],
};

describe("DevWorkflowStructuralNodePanel", () => {
	afterEach(() => {
		cleanup();
	});

	it("names a row-less dependency a template when the pinned graph says it is one", () => {
		// `isTemplate` is the SERVER's own TemplateSubtree verdict (Slice D). The client used to mirror that walk, and
		// two implementations of one rule are one drift away from disagreeing about a graph shape neither was tried on.
		renderWithProviders(
			<DevWorkflowStructuralNodePanel
				nodeRun={devWorkflowNodeRunDetail({ nodeKey: "join", nodeType: "Join", label: "Join" })}
				nodeType="Join"
				run={devWorkflowRun({
					graph,
					nodes: [devWorkflowNodeRunSummary({ id: "node-decompose", nodeKey: "decompose", label: "Decompose", status: "Succeeded" })],
				})}
			/>,
		);

		expect(screen.getByTestId("dev-workflow-node-dependency-validate").textContent).toContain("template");
		// The dependency that DID run reads as satisfied, not as a template — the two must never share a verdict.
		expect(screen.getByTestId("dev-workflow-node-dependency-decompose").textContent).toContain("satisfied");
	});

	it("does not call a row-less dependency a template when the graph never marked it one", () => {
		renderWithProviders(
			<DevWorkflowStructuralNodePanel
				nodeRun={devWorkflowNodeRunDetail({ nodeKey: "join", nodeType: "Join", label: "Join" })}
				nodeType="Join"
				run={devWorkflowRun({
					graph: { ...graph, nodes: graph.nodes?.map(({ isTemplate: _isTemplate, ...rest }) => rest) },
					nodes: [],
				})}
			/>,
		);

		expect(screen.getByTestId("dev-workflow-node-dependency-validate").textContent).not.toContain("template");
	});

	it("reads a skipped dependency under an All join by the SERVER's waived verdict, and a failed one as dead", () => {
		// C1: the state machine waives a skip a person chose, so the join carries on if a sibling arrived. Badging it
		// dead beside the branch that failed told an operator the two would do the same thing to the join, and only
		// one of them does. The verdict itself is `skipWaived` on the row — see the next case for why.
		renderWithProviders(
			<DevWorkflowStructuralNodePanel
				nodeRun={devWorkflowNodeRunDetail({ nodeKey: "join", nodeType: "Join", label: "Join" })}
				nodeType="Join"
				run={devWorkflowRun({
					graph: {
						schemaVersion: 1,
						nodes: [{ nodeKey: "join", nodeType: "Join", label: "Join" }],
						edges: [
							{ from: "excused", to: "join" },
							{ from: "broken", to: "join" },
						],
					},
					nodes: [
						devWorkflowNodeRunSummary({ id: "node-excused", nodeKey: "excused", label: "Excused", status: "Skipped", skipWaived: true }),
						devWorkflowNodeRunSummary({ id: "node-broken", nodeKey: "broken", label: "Broken", status: "Failed" }),
					],
				})}
			/>,
		);

		expect(screen.getByTestId("dev-workflow-node-dependency-excused").textContent).toContain("the join carries on if a sibling succeeded");
		expect(screen.getByTestId("dev-workflow-node-dependency-broken").textContent).toContain("the join skips once nothing is pending");
	});

	it("badges a skip that cascaded off a failure DEAD, though the failure is nowhere in this list", () => {
		// The shape the row cannot judge for itself: `broken` FAILED, `cascaded` was skipped by that failure, and the
		// join sits beside a sibling that succeeded. The runtime skips the join. Reading the row's own status the panel
		// said it would carry on — and `broken` is not among the join's dependencies for a reader to check. The server
		// sends `skipWaived: false`, and that is what the badge says.
		renderWithProviders(
			<DevWorkflowStructuralNodePanel
				nodeRun={devWorkflowNodeRunDetail({ nodeKey: "join", nodeType: "Join", label: "Join" })}
				nodeType="Join"
				run={devWorkflowRun({
					graph: {
						schemaVersion: 1,
						nodes: [{ nodeKey: "join", nodeType: "Join", label: "Join" }],
						edges: [
							{ from: "cascaded", to: "join" },
							{ from: "landed", to: "join" },
						],
					},
					nodes: [
						devWorkflowNodeRunSummary({ id: "node-cascaded", nodeKey: "cascaded", label: "Cascaded", status: "Skipped", skipWaived: false }),
						devWorkflowNodeRunSummary({ id: "node-landed", nodeKey: "landed", label: "Landed", status: "Succeeded" }),
					],
				})}
			/>,
		);

		const cascaded = screen.getByTestId("dev-workflow-node-dependency-cascaded").textContent ?? "";
		expect(cascaded).toContain("the join skips once nothing is pending");
		expect(cascaded).not.toContain("carries on");
	});

	it("claims nothing about the join for a skip an older server sent no verdict for", () => {
		// `skipWaived` is additive, so a client can outrun the server that answers it. Guessing either way is a claim
		// about what the join will do, and the wrong guess is the bug this replaced — so the badge just says "skipped".
		renderWithProviders(
			<DevWorkflowStructuralNodePanel
				nodeRun={devWorkflowNodeRunDetail({ nodeKey: "join", nodeType: "Join", label: "Join" })}
				nodeType="Join"
				run={devWorkflowRun({
					graph: {
						schemaVersion: 1,
						nodes: [{ nodeKey: "join", nodeType: "Join", label: "Join" }],
						edges: [{ from: "unjudged", to: "join" }],
					},
					nodes: [devWorkflowNodeRunSummary({ id: "node-unjudged", nodeKey: "unjudged", label: "Unjudged", status: "Skipped" })],
				})}
			/>,
		);

		const unjudged = screen.getByTestId("dev-workflow-node-dependency-unjudged").textContent ?? "";
		expect(unjudged).toContain("skipped");
		expect(unjudged).not.toContain("carries on");
		expect(unjudged).not.toContain("dead");
	});

	it("still reads a skipped dependency under an Any join as the branch that did not carry it", () => {
		// `Waived` is not `Satisfied`: a merge that exists to carry ONE live branch cannot be carried by a branch
		// nobody ran, so the wording under `Any` is unchanged.
		renderWithProviders(
			<DevWorkflowStructuralNodePanel
				nodeRun={devWorkflowNodeRunDetail({ nodeKey: "join", nodeType: "Join", label: "Join" })}
				nodeType="Join"
				run={devWorkflowRun({
					graph: {
						schemaVersion: 1,
						nodes: [{ nodeKey: "join", nodeType: "Join", label: "Join", joinPolicy: "Any" }],
						edges: [
							{ from: "excused", to: "join" },
							{ from: "taken", to: "join" },
						],
					},
					nodes: [
						devWorkflowNodeRunSummary({ id: "node-excused", nodeKey: "excused", label: "Excused", status: "Skipped" }),
						devWorkflowNodeRunSummary({ id: "node-taken", nodeKey: "taken", label: "Taken", status: "Succeeded" }),
					],
				})}
			/>,
		);

		expect(screen.getByTestId("dev-workflow-node-dependency-excused").textContent).toContain("this branch will not carry the join");
	});

	it("lists a gate's branches and the condition each one carries, verbatim", () => {
		renderWithProviders(
			<DevWorkflowStructuralNodePanel
				nodeRun={devWorkflowNodeRunDetail({ nodeKey: "decompose", nodeType: "Gate", label: "Decompose" })}
				nodeType="Gate"
				run={devWorkflowRun({
					graph: {
						schemaVersion: 1,
						nodes: [{ nodeKey: "decompose", nodeType: "Gate", label: "Decompose" }],
						edges: [{ from: "decompose", to: "join", condition: { path: "$.decision", op: "eq", value: "Approve" } }],
					},
					nodes: [devWorkflowNodeRunSummary({ id: "node-join", nodeKey: "join", label: "Join", status: "Succeeded" })],
				})}
			/>,
		);

		expect(screen.getByTestId("dev-workflow-node-branch-condition-join").textContent).toBe('$.decision eq "Approve"');
		expect(screen.getByTestId("dev-workflow-node-branch-taken-join")).toBeDefined();
	});
});
