// The four properties the layout has to hold. Three of them are the reason this is a module of its own rather than
// positions computed inside the view: a graph that jitters between two renders of the same nodes, loses a node, or
// hangs on a cycle is not something a component test would catch.

import { describe, expect, it } from "vitest";

import {
	type GraphWorkflowLayoutEdge,
	type GraphWorkflowLayoutNode,
	layoutGraphWorkflow,
	NODE_SPACING_Y,
	RANK_SPACING_X,
} from "@/features/graphWorkflows/models/GraphWorkflowLayout";

function nodes(...keys: readonly string[]): GraphWorkflowLayoutNode[] {
	return keys.map((key) => ({ key }));
}

function chain(...keys: readonly string[]): GraphWorkflowLayoutEdge[] {
	return keys.slice(1).map((to, index) => ({ from: keys[index] ?? "", to }));
}

describe("layoutGraphWorkflow", () => {
	it("ranks a linear chain 0..n with one node per rank", () => {
		const layout = layoutGraphWorkflow(nodes("start", "analyze", "check", "done"), chain("start", "analyze", "check", "done"));

		expect(layout.rankCount).toBe(4);
		expect(["start", "analyze", "check", "done"].map((key) => layout.positions.get(key))).toEqual([
			{ x: 0, y: 0, rank: 0, indexInRank: 0 },
			{ x: RANK_SPACING_X, y: 0, rank: 1, indexInRank: 0 },
			{ x: RANK_SPACING_X * 2, y: 0, rank: 2, indexInRank: 0 },
			{ x: RANK_SPACING_X * 3, y: 0, rank: 3, indexInRank: 0 },
		]);
		expect(layout.backEdgeKeys.size).toBe(0);
	});

	it("ranks a join after both branches, top-aligns the rank and separates the branches in y", () => {
		const layout = layoutGraphWorkflow(nodes("start", "left", "right", "merge"), [
			{ from: "start", to: "left" },
			{ from: "start", to: "right" },
			{ from: "left", to: "merge" },
			{ from: "right", to: "merge" },
		]);

		const left = layout.positions.get("left");
		const right = layout.positions.get("right");
		const merge = layout.positions.get("merge");
		expect([left?.rank, right?.rank]).toEqual([1, 1]);
		// Longest path, not shortest: the join sits past the whole fan-out rather than beside it.
		expect(merge?.rank).toBe(2);
		expect(Math.abs((left?.y ?? 0) - (right?.y ?? 0))).toBe(NODE_SPACING_Y);
		// Top-aligned: every rank starts at y 0, so the first branch and the join it closes into share the spine.
		expect(left?.y).toBe(0);
		expect(merge?.y).toBe(0);
	});

	it("is deterministic: the same graph in a different array order lays out identically", () => {
		const edges: GraphWorkflowLayoutEdge[] = [
			{ from: "start", to: "b" },
			{ from: "start", to: "a" },
			{ from: "a", to: "done" },
			{ from: "b", to: "done" },
		];
		const first = layoutGraphWorkflow(nodes("start", "a", "b", "done"), edges);
		const second = layoutGraphWorkflow(nodes("done", "b", "a", "start"), [...edges].reverse());

		for (const key of ["start", "a", "b", "done"]) {
			expect(second.positions.get(key), key).toEqual(first.positions.get(key));
		}
	});

	it("terminates on a cycle, places every node once, and tags only the edge that does not move forward", () => {
		const layout = layoutGraphWorkflow(nodes("a", "b", "c"), [...chain("a", "b", "c"), { from: "c", to: "b" }]);

		expect(layout.positions.size).toBe(3);
		expect(layout.positions.get("a")?.rank).toBe(0);
		expect(layout.backEdgeKeys.has("c>b")).toBe(true);
		// The edge INTO the cycle still reads forward — only the cycle itself is tagged.
		expect(layout.backEdgeKeys.has("a>b")).toBe(false);
	});

	it("places disconnected nodes side by side in rank 0 in key order", () => {
		const layout = layoutGraphWorkflow(nodes("zebra", "apple"), []);

		expect(layout.positions.get("apple")).toEqual({ x: 0, y: 0, rank: 0, indexInRank: 0 });
		expect(layout.positions.get("zebra")).toEqual({ x: 0, y: NODE_SPACING_Y, rank: 0, indexInRank: 1 });
	});

	it("returns an empty layout for an empty graph rather than a rank of nothing", () => {
		const layout = layoutGraphWorkflow([], []);

		expect(layout.rankCount).toBe(0);
		expect(layout.positions.size).toBe(0);
		expect(layout.backEdgeKeys.size).toBe(0);
	});
});
