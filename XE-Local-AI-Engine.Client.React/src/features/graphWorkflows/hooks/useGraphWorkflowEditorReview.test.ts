// @vitest-environment jsdom

// The editor's designer states: what may change the working graph while a turn is in flight or a proposal is under
// review (nothing), and what Accept, Undo, Reject and a stale proposal do to the canvas and its dirty check.

import { act, renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { useGraphWorkflowEditor } from "@/features/graphWorkflows/hooks/useGraphWorkflowEditor";
import { canvasToGraph, graphToCanvas, graphWorkflowsEqual } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { designerChanges, designerProposal, eightNodeGraph } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";

function renderEditor() {
	return renderHook(() => useGraphWorkflowEditor(eightNodeGraph));
}

type Editor = ReturnType<typeof renderEditor>["result"];

/** `editing` → `generating` → `reviewing` with the fixture proposal, made against the graph the editor holds. */
function propose(result: Editor, overrides: { readonly valid?: boolean; readonly basis?: typeof eightNodeGraph } = {}): void {
	act(() => {
		result.current.beginGenerating();
	});
	act(() => {
		result.current.showProposal({
			proposal: designerProposal,
			changes: designerChanges,
			valid: overrides.valid ?? true,
			errors: overrides.valid === false ? ["note: the instructions are empty"] : [],
			basis: overrides.basis ?? result.current.graph,
		});
	});
}

/** Every mutator the editor exposes, each aimed at something the fixture graph has. */
function mutateEverything(result: Editor): void {
	act(() => {
		result.current.addNode("Agent");
		result.current.updateNodeData("analyze", { label: "Changed" });
		result.current.renameNode("analyze", "analysis");
		result.current.updateEdgeData("e1", { label: "Changed" });
		result.current.updateSettings({ ...result.current.settings, kind: "Chat" });
		result.current.removeNode("lookup");
		result.current.removeEdge("e2");
		result.current.autoArrange();
		result.current.onConnect({ source: "start", target: "done", sourceHandle: null, targetHandle: null });
		result.current.onNodesChange([{ type: "remove", id: "merge" }]);
		result.current.onEdgesChange([{ type: "remove", id: "e8" }]);
	});
}

describe("useGraphWorkflowEditor designer states", () => {
	it.each(["generating", "reviewing"] as const)("changes nothing on the working graph while %s", (mode) => {
		const { result } = renderEditor();
		if (mode === "generating") {
			act(() => {
				result.current.beginGenerating();
			});
		} else {
			propose(result);
		}
		const before = result.current.graph;

		mutateEverything(result);

		expect(result.current.mode).toBe(mode);
		expect(graphWorkflowsEqual(result.current.graph, before)).toBe(true);
		expect(result.current.isDirty).toBe(false);
	});

	it("restores editing with the graph untouched when a turn is cancelled or fails", () => {
		const { result } = renderEditor();
		act(() => {
			result.current.beginGenerating();
		});

		act(() => {
			result.current.endGenerating();
		});

		expect(result.current.mode).toBe("editing");
		expect(result.current.isDirty).toBe(false);
		act(() => {
			result.current.removeEdge("e1");
		});
		expect(result.current.edges.some((edge) => edge.id === "e1")).toBe(false);
	});

	it("draws the proposal for review without touching the working graph, and Reject drops it", () => {
		const { result } = renderEditor();
		propose(result);

		expect(result.current.review?.canvas.nodes.map((node) => node.id)).toContain("note");
		expect(result.current.nodes.map((node) => node.id)).not.toContain("note");

		act(() => {
			result.current.rejectProposal();
		});

		expect(result.current.mode).toBe("editing");
		expect(result.current.review).toBeUndefined();
		expect(result.current.isDirty).toBe(false);
	});

	it("accepts into the canvas, keeps the saved baseline so the editor is dirty, and undoes in one step", () => {
		const { result } = renderEditor();
		propose(result);

		let accepted = false;
		act(() => {
			accepted = result.current.acceptProposal();
		});

		expect(accepted).toBe(true);
		expect(result.current.mode).toBe("editing");
		// The proposal exactly as loading it would put it on the canvas, parser defaults filled in.
		const loaded = graphToCanvas(designerProposal);
		expect(graphWorkflowsEqual(result.current.graph, canvasToGraph(loaded.nodes, loaded.edges, loaded.settings).graph)).toBe(
			true,
		);
		expect(result.current.isDirty).toBe(true);
		expect(result.current.canUndoAccept).toBe(true);

		act(() => {
			result.current.undoAccept();
		});

		expect(graphWorkflowsEqual(result.current.graph, eightNodeGraph)).toBe(true);
		expect(result.current.isDirty).toBe(false);
		expect(result.current.canUndoAccept).toBe(false);
	});

	it("withdraws Undo at the next edit, but not when React Flow only measures or selects", () => {
		const { result } = renderEditor();
		propose(result);
		act(() => {
			result.current.acceptProposal();
		});

		act(() => {
			result.current.onNodesChange([
				{ type: "dimensions", id: "note", dimensions: { width: 180, height: 60 } },
				{ type: "select", id: "note", selected: true },
			]);
		});
		expect(result.current.canUndoAccept).toBe(true);

		act(() => {
			result.current.updateNodeData("note", { label: "Edited after accept" });
		});
		expect(result.current.canUndoAccept).toBe(false);
	});

	it("withdraws Undo on save", () => {
		const { result } = renderEditor();
		propose(result);
		act(() => {
			result.current.acceptProposal();
		});

		act(() => {
			result.current.markSaved(result.current.graph);
		});

		expect(result.current.canUndoAccept).toBe(false);
		expect(result.current.isDirty).toBe(false);
	});

	it("refuses to accept a proposal that failed validation", () => {
		const { result } = renderEditor();
		propose(result, { valid: false });

		let accepted = true;
		act(() => {
			accepted = result.current.acceptProposal();
		});

		expect(accepted).toBe(false);
		expect(result.current.mode).toBe("reviewing");
		expect(graphWorkflowsEqual(result.current.graph, eightNodeGraph)).toBe(true);
	});

	it("marks a proposal made against another graph stale and refuses to accept it", () => {
		const { result } = renderEditor();
		// The basis is the graph the request was SENT with; one the canvas no longer holds is what a late answer carries.
		propose(result, { basis: { ...eightNodeGraph, edges: (eightNodeGraph.edges ?? []).slice(1) } });

		expect(result.current.isReviewStale).toBe(true);
		let accepted = true;
		act(() => {
			accepted = result.current.acceptProposal();
		});
		expect(accepted).toBe(false);
		expect(graphWorkflowsEqual(result.current.graph, eightNodeGraph)).toBe(true);
	});

	it("drops the review and the Undo offer when another graph is loaded", () => {
		const { result } = renderEditor();
		propose(result);

		act(() => {
			result.current.reset(designerProposal);
		});

		expect(result.current.mode).toBe("editing");
		expect(result.current.review).toBeUndefined();
		expect(result.current.canUndoAccept).toBe(false);
	});
});
