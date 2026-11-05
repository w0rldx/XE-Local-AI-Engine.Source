// The dirty check: what counts as a real edit to a stored graph, and what does not.

import { describe, expect, it } from "vitest";

import { graphWorkflowsEqual } from "@/features/graphWorkflows/models/GraphWorkflowEquality";
import type { GraphWorkflowGraph } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { eightNodeGraph, wireGraph } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";

describe("graphWorkflowsEqual", () => {
	it("is order-independent over nodes and edges", () => {
		const reversed: GraphWorkflowGraph = {
			schemaVersion: 1,
			nodes: [...(eightNodeGraph.nodes ?? [])].reverse(),
			edges: [...(eightNodeGraph.edges ?? [])].reverse(),
		};

		expect(graphWorkflowsEqual(reversed, eightNodeGraph)).toBe(true);
	});

	it("sees a moved node, unlike Preview, because this wire persists position", () => {
		const moved = structuredClone(eightNodeGraph);
		const node = (moved.nodes ?? [])[0];
		if (node) {
			node.position = { x: 40, y: 0 };
		}

		expect(graphWorkflowsEqual(moved, eightNodeGraph)).toBe(false);
	});

	it("reads absent, null and the parser's default as the same graph", () => {
		const explicit = wireGraph(
			[{ key: "start", kind: "Start", label: "", position: { x: 0, y: 0 }, joinPolicy: "All", config: { defaultInput: null } }],
			[{ key: "e1", from: "start", to: "start", label: "" }],
		);
		const terse: GraphWorkflowGraph = {
			nodes: [{ key: "start", kind: "Start", position: { x: 0, y: 0 }, config: {} }],
			edges: [{ key: "e1", from: "start", to: "start" }],
		};

		expect(graphWorkflowsEqual(explicit, terse)).toBe(true);
	});

	it("does not read a missing position as the origin — a laid-out node is a real edit", () => {
		const placed = wireGraph([{ key: "start", kind: "Start", position: { x: 0, y: 0 }, config: {} }], []);
		const unplaced = wireGraph([{ key: "start", kind: "Start", config: {} }], []);

		expect(graphWorkflowsEqual(placed, unplaced)).toBe(false);
	});

	it("sees a changed edge label, sourceHandle and condition", () => {
		const base = wireGraph(
			[{ key: "check", kind: "Condition", position: { x: 0, y: 0 }, config: { path: "a" } }],
			[{ key: "e1", from: "check", to: "check", label: "yes", sourceHandle: "true", condition: { op: "Eq", value: true } }],
		);

		for (const change of [{ label: "no" }, { sourceHandle: "false" }, { condition: { op: "Ne", value: true } }] as const) {
			const mutated = wireGraph(base.nodes ?? [], [{ ...(base.edges ?? [])[0], ...change }]);
			expect(graphWorkflowsEqual(mutated, base), JSON.stringify(change)).toBe(false);
		}
	});

	it("treats two empty graphs as equal and an empty one as different from a populated one", () => {
		expect(graphWorkflowsEqual(undefined, { schemaVersion: 1, nodes: [], edges: [] })).toBe(true);
		expect(graphWorkflowsEqual(undefined, eightNodeGraph)).toBe(false);
	});

	it("sees a config edit inside a nested document that only differs by a null member", () => {
		const withNull = wireGraph([{ key: "a", kind: "Tool", position: { x: 0, y: 0 }, config: { arguments: { path: null } } }], []);
		const without = wireGraph([{ key: "a", kind: "Tool", position: { x: 0, y: 0 }, config: { arguments: {} } }], []);

		// A null INSIDE the operator's own document is data; only the config's own top-level members are defaulted away.
		expect(graphWorkflowsEqual(withNull, without)).toBe(false);
	});
});
