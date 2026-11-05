// The `context` edges drawn around a Pause, and the guards that keep them from starving or skipping the node they feed.

import { describe, expect, it } from "vitest";

import { defaultNodeData } from "@/features/graphWorkflows/models/GraphWorkflowAuthoring";
import {
	type GraphWorkflowCanvas,
	type GraphWorkflowCanvasEdge,
	type GraphWorkflowCanvasNode,
	graphWorkflowNodeTypeByKind,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import type { GraphWorkflowNodeKind } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { pauseContextEdges } from "@/features/graphWorkflows/models/GraphWorkflowPauseContext";

describe("pauseContextEdges", () => {
	/** A canvas from `key:Kind[:JoinPolicy]` node specs and `from>to` edge specs; the editor's shape, minus positions. */
	function canvasOf(nodeSpecs: readonly string[], edgeSpecs: readonly string[]): GraphWorkflowCanvas {
		const nodes = nodeSpecs.map((spec): GraphWorkflowCanvasNode => {
			const [key = "", kind = "Agent", joinPolicy] = spec.split(":");
			const data = defaultNodeData(kind as GraphWorkflowNodeKind, key);
			return {
				id: key,
				type: graphWorkflowNodeTypeByKind[data.kind],
				position: { x: 0, y: 0 },
				data: joinPolicy === "Any" ? { ...data, joinPolicy: "Any" } : data,
			};
		});
		const edges = edgeSpecs.map((spec, index): GraphWorkflowCanvasEdge => {
			const [source = "", target = ""] = spec.split(">");
			return { id: `e${index + 1}`, source, target, data: {} };
		});
		return { nodes, edges };
	}

	function addedPairs(nodeSpecs: readonly string[], edgeSpecs: readonly string[]): string[] {
		const canvas = canvasOf(nodeSpecs, edgeSpecs);
		return pauseContextEdges(canvas.nodes, canvas.edges).map((edge) => `${edge.source}>${edge.target}`);
	}

	const chain = ["start:Start", "a:Agent", "hold:Pause", "b:Agent", "done:End"];

	it("routes the answer around a Pause, from the Pause's own predecessor", () => {
		const canvas = canvasOf(chain, ["start>a", "a>hold", "hold>b", "b>done"]);
		const added = pauseContextEdges(canvas.nodes, canvas.edges);

		expect(added).toHaveLength(1);
		expect(added[0]).toMatchObject({ source: "a", target: "b", label: "context" });
		expect(added[0]?.data).toEqual({ label: "context" });
		// A key from the ONE namespace the graph uses, so the new edge cannot collide with a node or an existing edge.
		expect(added[0]?.id).toBe("e5");
		expect(added[0]?.data?.condition).toBeUndefined();
	});

	it("adds the edge whichever side of the Pause was wired last", () => {
		// The successor first, then the predecessor: both orders reach the same graph, so both must reach the same edge.
		expect(addedPairs(chain, ["start>a", "hold>b", "a>hold", "b>done"])).toEqual(["a>b"]);
	});

	it("does not duplicate an edge the author already drew", () => {
		expect(addedPairs(chain, ["start>a", "a>hold", "hold>b", "a>b", "b>done"])).toEqual([]);
	});

	it("walks through consecutive Pause nodes to the nearest non-Pause ancestor", () => {
		const nodes = ["start:Start", "a:Agent", "first:Pause", "second:Pause", "b:Agent", "done:End"];

		// One rule applied to every starved successor: the second Pause also gets to see the content it is approving,
		// so `a` reaches both it and the node behind it. In successor-key order, which is the order the pass draws in.
		expect(addedPairs(nodes, ["start>a", "a>first", "first>second", "second>b", "b>done"])).toEqual(["a>b", "a>second"]);
	});

	it("routes the content around a ChatInput exactly as around a Pause", () => {
		const nodes = ["start:Start", "a:Agent", "ask:ChatInput", "b:Agent", "done:End"];

		expect(addedPairs(nodes, ["start>a", "a>ask", "ask>b", "b>done"])).toEqual(["a>b"]);
	});

	it("treats Start as a fine ancestor, because its output is the run's input", () => {
		expect(addedPairs(["start:Start", "hold:Pause", "b:Agent", "done:End"], ["start>hold", "hold>b", "b>done"])).toEqual([
			"start>b",
		]);
	});

	it("leaves a successor something other than a Pause already feeds alone, and adds nothing without a Pause", () => {
		// `b` is not starved: `c` already hands it content, so its `input` is the `upstream` map either way and a third
		// inbound edge would only give its `All` policy one more branch to wait for. `PauseContextWarnings` does not
		// warn about a Y like this one, and the two have to agree about which graphs need an edge.
		expect(addedPairs([...chain, "c:Agent"], ["start>a", "a>hold", "hold>b", "c>b", "b>done"])).toEqual([]);
		expect(addedPairs(["start:Start", "a:Agent", "done:End"], ["start>a", "a>done"])).toEqual([]);
	});

	// A Condition's out-edges carry a `true`/`false` handle; an added edge carries none, so it would save as a SECOND
	// unconditional branch out of that Condition — refused by `conditionMultipleDefaults` here and by the server there.
	it("skips a Condition ancestor rather than giving it a second unconditional branch", () => {
		const nodes = ["start:Start", "a:Agent", "check:Condition", "hold:Pause", "b:Agent", "c:Agent", "done:End"];
		const edges = ["start>a", "a>check", "check>hold", "check>c", "hold>b", "b>done", "c>done"];

		expect(addedPairs(nodes, edges)).toEqual([]);
	});

	// An `Any` policy fires on the FIRST satisfied branch, so an unconditional content edge into one is satisfied even
	// when every approval was rejected: the node would run past the Pause instead of waiting on it. The policy is a
	// member of every node, and `GraphWorkflowStateMachine.Admission` reads it off whatever it is admitting.
	it("skips an Any successor of any kind and still feeds an All one", () => {
		const edges = ["start>a", "a>hold", "hold>merge", "merge>done"];
		const skipped = [
			["start:Start", "a:Agent", "hold:Pause", "merge:Join:Any", "done:End"],
			["start:Start", "a:Agent", "hold:Pause", "merge:Agent:Any", "done:End"],
			["start:Start", "a:Agent", "hold:Pause", "merge:End:Any", "done:End"],
		];

		for (const nodes of skipped) {
			expect(addedPairs(nodes, edges), nodes[3]).toEqual([]);
		}
		expect(addedPairs(["start:Start", "a:Agent", "hold:Pause", "merge:Join", "done:End"], edges)).toEqual(["a>merge"]);
		expect(addedPairs(["start:Start", "a:Agent", "hold:Pause", "merge:Agent", "done:End"], edges)).toEqual(["a>merge"]);
	});

	// Two ancestors are two branches, and only one of them runs. An `All` successor with a DEAD inbound edge is
	// skipped, so a second context edge would delete the very node the affordance is there to feed.
	it("leaves a Pause with more than one nearest ancestor alone", () => {
		const nodes = ["start:Start", "check:Condition", "a:Agent", "a2:Agent", "hold:Pause", "b:Agent", "done:End"];
		const edges = ["start>check", "check>a", "check>a2", "a>hold", "a2>hold", "hold>b", "b>done"];

		expect(addedPairs(nodes, edges)).toEqual([]);
	});

	it("adds nothing for a Pause with no successor and none for a Pause with no ancestor", () => {
		expect(addedPairs(["start:Start", "a:Agent", "hold:Pause"], ["start>a", "a>hold"])).toEqual([]);
		expect(addedPairs(["hold:Pause", "b:Agent", "done:End"], ["hold>b", "b>done"])).toEqual([]);
	});

	it("considers only what the one connection can have broken", () => {
		const nodes = ["start:Start", "a:Agent", "first:Pause", "b:Agent", "second:Pause", "c:Agent", "done:End"];
		const edges = ["start>a", "a>first", "first>b", "b>second", "second>c", "c>done"];
		const canvas = canvasOf(nodes, edges);
		const pairsFor = (from: string, to: string) =>
			pauseContextEdges(canvas.nodes, canvas.edges, { from, to }).map((edge) => `${edge.source}>${edge.target}`);

		// Wiring OUT of a pause can only starve the node just connected, so `first` is left alone even though it is
		// missing its edge too — the operator may have deleted that one on purpose.
		expect(pairsFor("second", "c")).toEqual(["b>c"]);
		// Wiring INTO a pause can starve any of its successors, so all of them are considered.
		expect(pairsFor("a", "first")).toEqual(["a>b"]);
		// The whole-graph pass, which is what the importer does.
		expect(addedPairs(nodes, edges)).toEqual(["a>b", "b>c"]);
	});

	// The reverse-order case: with `first → second → b` already drawn, wiring `a → first` is the only gesture that will
	// ever consider `b`. The whole-graph pass would visit `second` on its own; the connection-scoped one never does.
	it("walks through a Pause successor when the gesture wires into the Pause before it", () => {
		const nodes = ["start:Start", "a:Agent", "first:Pause", "second:Pause", "b:Agent", "done:End"];
		const canvas = canvasOf(nodes, ["start>a", "first>second", "second>b", "b>done", "a>first"]);

		expect(pauseContextEdges(canvas.nodes, canvas.edges, { from: "a", to: "first" }).map((edge) => edge.target)).toEqual([
			"b",
			"second",
		]);
	});

	// The walk only nominates candidates. `b` is behind `second`, and `second` is fed by `a` AND `x`, so nothing can be
	// named for `b` without risking a dead branch — and `second` itself is not starved, because `x` already feeds it.
	// `PauseContextWarnings` answers the same graph the same way, and the two must not disagree.
	it("judges a successor of the walk on its own ancestry, not the wired Pause's", () => {
		const nodes = ["start:Start", "a:Agent", "x:Agent", "first:Pause", "second:Pause", "b:Agent", "done:End"];
		const edges = ["start>a", "start>x", "first>second", "x>second", "second>b", "b>done", "a>first"];
		const canvas = canvasOf(nodes, edges);

		expect(pauseContextEdges(canvas.nodes, canvas.edges, { from: "a", to: "first" })).toEqual([]);
	});

	it("stops on a Pause wired back into itself instead of walking forever", () => {
		expect(addedPairs(["hold:Pause", "b:Agent", "done:End"], ["hold>hold", "hold>b", "b>done"])).toEqual([]);
	});
});
