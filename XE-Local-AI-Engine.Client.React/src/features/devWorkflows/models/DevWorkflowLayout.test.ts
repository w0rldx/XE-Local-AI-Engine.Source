// The four properties P4 §2.3.2 asks the layout to hold. Three of them are the reason this is a module of its own
// rather than positions computed inside the view: a graph that jitters on a status tick, reorders a materialized
// sibling group, or hangs on a cycle is not something a component test would catch.

import { describe, expect, it } from "vitest";

import {
	type DevWorkflowLayoutEdge,
	type DevWorkflowLayoutNode,
	layoutDevWorkflowGraph,
} from "@/features/devWorkflows/models/DevWorkflowLayout";

function node(id: string, overrides: Partial<DevWorkflowLayoutNode> = {}): DevWorkflowLayoutNode {
	return { id, nodeKey: id, ...overrides };
}

function chain(...ids: readonly string[]): DevWorkflowLayoutEdge[] {
	return ids.slice(1).map((to, index) => ({ from: ids[index] ?? "", to }));
}

describe("layoutDevWorkflowGraph", () => {
	it("ranks a linear chain 0..n with one node per rank", () => {
		const layout = layoutDevWorkflowGraph(
			[node("research"), node("plan"), node("build"), node("review")],
			chain("research", "plan", "build", "review"),
		);

		expect(layout.rankCount).toBe(4);
		expect(["research", "plan", "build", "review"].map((id) => layout.positions.get(id))).toEqual([
			{ x: 0, y: 0, rank: 0, indexInRank: 0 },
			{ x: 280, y: 0, rank: 1, indexInRank: 0 },
			{ x: 560, y: 0, rank: 2, indexInRank: 0 },
			{ x: 840, y: 0, rank: 3, indexInRank: 0 },
		]);
		expect(layout.backEdgeKeys.size).toBe(0);
	});

	it("ranks a join after both of its branches and separates the branches in y", () => {
		const layout = layoutDevWorkflowGraph([node("start"), node("left"), node("right"), node("join")], [
			{ from: "start", to: "left" },
			{ from: "start", to: "right" },
			{ from: "left", to: "join" },
			{ from: "right", to: "join" },
		]);

		const left = layout.positions.get("left");
		const right = layout.positions.get("right");
		const join = layout.positions.get("join");
		expect([left?.rank, right?.rank]).toEqual([1, 1]);
		// Longest path, not shortest: the join sits past the whole fan-out rather than beside it.
		expect(join?.rank).toBe(2);
		expect(left?.y).not.toBe(right?.y);
		expect(Math.abs((left?.y ?? 0) - (right?.y ?? 0))).toBe(130);
		// Top-aligned (R-C5): every rank starts at y 0, so the first branch and the join it closes into share the spine
		// and the second branch hangs below it. Under the centred formula this rank straddled y 0 instead; the assertion
		// above holds either way, and this one is the one that records which formula is in force.
		expect(left?.y).toBe(0);
		expect(join?.y).toBe(0);
	});

	it("leaves every pre-existing node where it was when a node materializes its children", () => {
		const nodes = [node("research"), node("plan"), node("decompose")];
		const edges = chain("research", "plan", "decompose");
		const before = layoutDevWorkflowGraph(nodes, edges);

		const children = Array.from({ length: 5 }, (_, index) =>
			node(`child-${index}`, {
				nodeKey: "task",
				materializationGroupKey: "decompose",
				materializationIndex: index,
			}),
		);
		const after = layoutDevWorkflowGraph(
			[...nodes, ...children],
			[...edges, ...children.map((child) => ({ from: "decompose", to: child.id }))],
		);

		for (const existing of nodes) {
			expect(after.positions.get(existing.id)).toEqual(before.positions.get(existing.id));
		}
		// And the children keep the order the server materialized them in, not the order they arrived in.
		expect(children.map((child) => after.positions.get(child.id)?.indexInRank)).toEqual([0, 1, 2, 3, 4]);
	});

	it("leaves a pre-existing node where it was when materialized children land in ITS rank", () => {
		// Slice C's shape, and the one the append-a-rank test above does not reach: `decompose`'s children arrive in a
		// rank that is already occupied — here by `publish`, which has held its row since the run started. The old
		// centred formula divided each rank by its own population, so `publish` was dragged upwards the instant it had
		// company; ordering the clones by barycenter alone would have pushed it down the rank instead.
		const nodes = [node("plan"), node("decompose"), node("docs"), node("publish")];
		const edges: DevWorkflowLayoutEdge[] = [
			{ from: "plan", to: "decompose" },
			{ from: "plan", to: "docs" },
			{ from: "docs", to: "publish" },
		];
		const before = layoutDevWorkflowGraph(nodes, edges);
		expect(before.positions.get("publish")?.rank).toBe(2);

		const children = Array.from({ length: 3 }, (_, index) =>
			node(`implement-${index}`, {
				nodeKey: `implement#${index}`,
				materializationGroupKey: "decompose",
				materializationIndex: index,
			}),
		);
		const after = layoutDevWorkflowGraph(
			[...nodes, ...children],
			[...edges, ...children.map((child) => ({ from: "decompose", to: child.id }))],
		);

		// The children really did land in `publish`'s rank — otherwise this test is the append case wearing a disguise.
		expect(children.map((child) => after.positions.get(child.id)?.rank)).toEqual([2, 2, 2]);
		for (const existing of nodes) {
			expect(after.positions.get(existing.id), existing.id).toEqual(before.positions.get(existing.id));
		}
		// And they hang below it in materialization order, as one contiguous block.
		expect(children.map((child) => after.positions.get(child.id)?.y)).toEqual([130, 260, 390]);
	});

	it("terminates on a cycle, ranks every node once, and tags the edge that does not move forward", () => {
		const layout = layoutDevWorkflowGraph(
			[node("a"), node("b"), node("c")],
			[...chain("a", "b", "c"), { from: "c", to: "b" }],
		);

		expect(layout.positions.size).toBe(3);
		expect(layout.positions.get("a")?.rank).toBe(0);
		expect(layout.backEdgeKeys.has("c>b")).toBe(true);
		// The edge into the cycle still reads forward — only the cycle itself is tagged.
		expect(layout.backEdgeKeys.has("a>b")).toBe(false);
	});
});
