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
