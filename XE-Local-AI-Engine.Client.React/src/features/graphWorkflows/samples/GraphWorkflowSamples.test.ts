// The shipped samples are posted verbatim by the create dialog, so each one must be a graph the editor opens clean and
// the client validator accepts. The backend's GraphWorkflowSampleContractTests runs the same files through the server
// parser, which is the authority; this file holds the client half and the picker's i18n contract.

import { describe, expect, it } from "vitest";

import { canvasToGraph, graphToCanvas, graphWorkflowsEqual } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { validateGraphWorkflowGraph } from "@/features/graphWorkflows/models/GraphWorkflowValidation";
import { GRAPH_WORKFLOW_SAMPLES } from "@/features/graphWorkflows/samples/GraphWorkflowSamples";
import en from "@/locales/en.json";

function bundleString(key: string): unknown {
	return key.split(".").reduce<unknown>((node, segment) => (node as Record<string, unknown> | undefined)?.[segment], en);
}

describe("GRAPH_WORKFLOW_SAMPLES", () => {
	it("ships the five samples under unique ids", () => {
		const ids = GRAPH_WORKFLOW_SAMPLES.map((sample) => sample.id);

		expect(ids).toEqual([
			"summarize-with-approval",
			"triage-and-route",
			"parallel-perspectives",
			"brief-with-review-loop",
			"agent-research-and-check",
		]);
		expect(new Set(ids).size).toBe(ids.length);
	});

	describe.each(GRAPH_WORKFLOW_SAMPLES)("$id", (sample) => {
		it("passes the client validator with no issues", () => {
			expect(validateGraphWorkflowGraph(sample.graph)).toEqual([]);
		});

		it("round-trips through the canvas unchanged, so it opens clean", () => {
			const canvas = graphToCanvas(sample.graph);
			const { graph, issues } = canvasToGraph(canvas.nodes, canvas.edges, canvas.settings);

			expect(issues).toEqual([]);
			expect(graphWorkflowsEqual(graph, sample.graph)).toBe(true);
		});

		it("gives every node a position, so nothing is laid out and dirty on open", () => {
			expect((sample.graph.nodes ?? []).every((node) => node.position !== undefined && node.position !== null)).toBe(true);
		});

		it("carries an object default input on its Start node", () => {
			const start = (sample.graph.nodes ?? []).find((node) => node.kind === "Start");
			const defaultInput = (start?.config as Record<string, unknown> | undefined)?.["defaultInput"];

			expect(typeof defaultInput).toBe("object");
			expect(defaultInput).not.toBeNull();
			expect(Array.isArray(defaultInput)).toBe(false);
		});

		// An Agent node is fine only unbound: it then runs the built-in default persona with the local tools.
		it("runs on a local GGUF alone: no Tool or ChatInput node, no bound agent, no model pin, a Standard graph", () => {
			const nodes = sample.graph.nodes ?? [];
			const config = (node: (typeof nodes)[number]) => node.config as Record<string, unknown> | undefined;

			expect(sample.graph.kind).toBeUndefined();
			expect(nodes.filter((node) => ["Tool", "ChatInput"].includes(node.kind ?? ""))).toEqual([]);
			expect(nodes.filter((node) => node.kind === "Agent" && config(node)?.["agentDefinitionId"] != null)).toEqual([]);
			// null is the editor's own spelling of "no pin" on an Agent node, so only a value counts as one.
			expect(nodes.filter((node) => config(node)?.["model"] != null)).toEqual([]);
		});

		it("names and describes itself with en bundle strings that match its defaults", () => {
			expect(bundleString(sample.nameKey)).toBe(sample.defaultName);
			expect(bundleString(sample.descriptionKey)).toBe(sample.defaultDescription);
		});
	});
});
