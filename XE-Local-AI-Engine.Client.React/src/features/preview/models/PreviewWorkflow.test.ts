import { describe, expect, it } from "vitest";

import { canvasToGraph, graphsEqual, graphToCanvas } from "@/features/preview/models/PreviewCanvasModels";
import type { PreviewWorkflowGraph } from "@/features/preview/models/PreviewWorkflowModels";
import { validatePreviewGraph } from "@/features/preview/models/PreviewWorkflowValidation";

const LINEAR_GRAPH: PreviewWorkflowGraph = {
	startText: "hello",
	nodes: [
		{ id: "start", kind: "Start" },
		{ id: "agent", kind: "Agent", label: "A", model: "qwen3:8b", instructions: "Respond." },
		{ id: "end", kind: "End" },
	],
	edges: [
		{ sourceId: "start", targetId: "agent" },
		{ sourceId: "agent", targetId: "end" },
	],
};

describe("preview graph mappers", () => {
	it("round-trips a graph through canvas and back, preserving wire field names", () => {
		const { nodes, edges } = graphToCanvas(LINEAR_GRAPH);
		const back = canvasToGraph(nodes, edges, LINEAR_GRAPH.startText);

		expect(back.startText).toBe("hello");
		expect(back.nodes).toHaveLength(3);
		expect(back.edges).toEqual([
			{ sourceId: "start", targetId: "agent" },
			{ sourceId: "agent", targetId: "end" },
		]);
		const agent = back.nodes.find((node) => node.kind === "Agent");
		expect(agent?.model).toBe("qwen3:8b");
		expect(agent?.instructions).toBe("Respond.");
	});

	it("emits only id + kind for non-Agent nodes (no stray agent fields)", () => {
		const { nodes, edges } = graphToCanvas(LINEAR_GRAPH);
		const back = canvasToGraph(nodes, edges, "");
		const start = back.nodes.find((node) => node.kind === "Start");
		expect(start).toEqual({ id: "start", kind: "Start" });
	});
});

describe("graphsEqual", () => {
	it("treats an identical graph as equal", () => {
		expect(graphsEqual(LINEAR_GRAPH, structuredClone(LINEAR_GRAPH))).toBe(true);
	});

	it("ignores node and edge ordering (positions are view-only, sets compared)", () => {
		const reordered: PreviewWorkflowGraph = {
			startText: LINEAR_GRAPH.startText,
			nodes: [...LINEAR_GRAPH.nodes].reverse(),
			edges: [...LINEAR_GRAPH.edges].reverse(),
		};
		expect(graphsEqual(LINEAR_GRAPH, reordered)).toBe(true);
	});

	it("detects an edited agent field (dirty canvas)", () => {
		const edited: PreviewWorkflowGraph = {
			...LINEAR_GRAPH,
			nodes: LINEAR_GRAPH.nodes.map((node) =>
				node.kind === "Agent" ? { ...node, instructions: "Changed." } : node,
			),
		};
		expect(graphsEqual(LINEAR_GRAPH, edited)).toBe(false);
	});

	it("detects a changed start text", () => {
		expect(graphsEqual(LINEAR_GRAPH, { ...LINEAR_GRAPH, startText: "different" })).toBe(false);
	});

	it("detects an added edge", () => {
		const withExtraEdge: PreviewWorkflowGraph = {
			...LINEAR_GRAPH,
			edges: [...LINEAR_GRAPH.edges, { sourceId: "start", targetId: "end" }],
		};
		expect(graphsEqual(LINEAR_GRAPH, withExtraEdge)).toBe(false);
	});
});

describe("validatePreviewGraph", () => {
	it("accepts a linear Start → Agent → End chain", () => {
		expect(validatePreviewGraph(LINEAR_GRAPH).isValid).toBe(true);
	});

	it("rejects a Start → End chain with no agent", () => {
		const result = validatePreviewGraph({
			startText: "",
			nodes: [
				{ id: "start", kind: "Start" },
				{ id: "end", kind: "End" },
			],
			edges: [{ sourceId: "start", targetId: "end" }],
		});
		expect(result.isValid).toBe(false);
		expect(result.errorKeys).toContain("pages.preview.validation.noAgent");
	});

	it("rejects an Agent node missing a model", () => {
		const result = validatePreviewGraph({
			startText: "",
			nodes: [
				{ id: "start", kind: "Start" },
				{ id: "agent", kind: "Agent", instructions: "Respond." },
				{ id: "end", kind: "End" },
			],
			edges: [
				{ sourceId: "start", targetId: "agent" },
				{ sourceId: "agent", targetId: "end" },
			],
		});
		expect(result.isValid).toBe(false);
		expect(result.errorKeys).toContain("pages.preview.validation.agentModel");
	});

	it("rejects a fan-out (non-linear) graph", () => {
		const result = validatePreviewGraph({
			startText: "",
			nodes: [
				{ id: "start", kind: "Start" },
				{ id: "a", kind: "Agent", model: "m", instructions: "i" },
				{ id: "b", kind: "Agent", model: "m", instructions: "i" },
				{ id: "end", kind: "End" },
			],
			edges: [
				{ sourceId: "start", targetId: "a" },
				{ sourceId: "start", targetId: "b" },
			],
		});
		expect(result.isValid).toBe(false);
		expect(result.errorKeys).toContain("pages.preview.validation.notLinear");
	});
});
