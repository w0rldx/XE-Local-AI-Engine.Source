// The share file is what leaves one node and arrives on another, so what is pinned is the shape check that runs before
// the import dialog opens (every refusal reason, and a good file), the download name the export route also uses, and
// that every graph this repo ships survives export → file → import → canvas → wire unchanged.

import { describe, expect, it } from "vitest";

import { canvasToGraph, graphToCanvas, graphWorkflowsEqual } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import {
	GRAPH_WORKFLOW_EXPORT_FORMAT,
	GRAPH_WORKFLOW_EXPORT_FORMAT_VERSION,
	type GraphWorkflowExportEnvelope,
	graphWorkflowAllowedUrls,
	graphWorkflowImportRequestBytes,
	GRAPH_WORKFLOW_IMPORT_MAX_BYTES,
	graphWorkflowExportFileName,
	parseGraphWorkflowExportFile,
} from "@/features/graphWorkflows/models/GraphWorkflowExportFormat";
import type { GraphWorkflowGraph } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { GRAPH_WORKFLOW_SAMPLES } from "@/features/graphWorkflows/samples/GraphWorkflowSamples";

const graph: GraphWorkflowGraph = { schemaVersion: 1, nodes: [], edges: [] };

function file(overrides: Record<string, unknown>): string {
	return JSON.stringify({
		format: GRAPH_WORKFLOW_EXPORT_FORMAT,
		formatVersion: 1,
		name: "N",
		description: null,
		graph,
		...overrides,
	});
}

describe("parseGraphWorkflowExportFile", () => {
	it.each([
		["notJson", "{ not json"],
		["wrongFormat", "[]"],
		["wrongFormat", file({ format: "something-else" })],
		["unsupportedVersion", file({ formatVersion: 2 })],
		["unsupportedVersion", file({ formatVersion: "1" })],
		["graphNotObject", file({ graph: [] })],
		["graphNotObject", file({ graph: null })],
		["graphMalformed", file({ graph: { schemaVersion: 1, nodes: {}, edges: [] } })],
		["graphMalformed", file({ graph: { schemaVersion: 1, nodes: [] } })],
	])("refuses %s", (reason, text) => {
		expect(parseGraphWorkflowExportFile(text)).toEqual({ ok: false, reason });
	});

	it("reads a good file, and a missing name or description as empty", () => {
		expect(parseGraphWorkflowExportFile(file({ name: "Report", description: "D" }))).toEqual({
			ok: true,
			envelope: { format: GRAPH_WORKFLOW_EXPORT_FORMAT, formatVersion: 1, name: "Report", description: "D", graph },
		});
		const bare = parseGraphWorkflowExportFile(JSON.stringify({ format: GRAPH_WORKFLOW_EXPORT_FORMAT, formatVersion: 1, graph }));
		expect(bare.ok && bare.envelope.name).toBe("");
		expect(bare.ok && bare.envelope.description).toBeNull();
	});
});

describe("graphWorkflowExportFileName", () => {
	it.each([
		["Übersicht für Märkte", "ubersicht-fur-markte.graph-workflow.json"],
		["  --Nightly   Triage!! ", "nightly-triage.graph-workflow.json"],
		["日本語", "graph-workflow.graph-workflow.json"],
		["a".repeat(70), `${"a".repeat(64)}.graph-workflow.json`],
	])("names %j as %s", (name, expected) => {
		expect(graphWorkflowExportFileName(name)).toBe(expected);
	});
});

describe("graphWorkflowAllowedUrls", () => {
	it("lists every Tool node's prefixes once and ignores other kinds", () => {
		const tools: GraphWorkflowGraph = {
			schemaVersion: 1,
			nodes: [
				{ key: "a", kind: "Tool", config: { toolName: "web_fetch", allowedUrls: ["https://a.example/", "https://b.example/"] } },
				{ key: "b", kind: "Tool", config: { toolName: "web_fetch", allowedUrls: ["https://a.example/"] } },
				{ key: "c", kind: "LlmCall", config: { allowedUrls: ["https://ignored.example/"] } },
			],
			edges: [],
		};
		expect(graphWorkflowAllowedUrls(tools)).toEqual(["https://a.example/", "https://b.example/"]);
		expect(graphWorkflowAllowedUrls(graph)).toEqual([]);
	});

	// The dialog reads this off a file nobody has validated, during render: malformed input must read as "none".
	it("reads a non-list node set or a null node as no prefixes instead of throwing", () => {
		const malformed = (nodes: unknown) => ({ schemaVersion: 1, nodes, edges: [] }) as unknown as GraphWorkflowGraph;

		expect(graphWorkflowAllowedUrls(malformed({}))).toEqual([]);
		expect(graphWorkflowAllowedUrls(malformed([null, 7, { kind: "Tool", config: null }]))).toEqual([]);
	});

	// The server accepts "tool" and "TOOL" as the Tool kind, so a file spelled that way still imports and fetches.
	it("reads a Tool node whose kind is not spelled canonically", () => {
		const spelled = (kind: string) =>
			({
				schemaVersion: 1,
				nodes: [{ key: "a", kind, config: { toolName: "web_fetch", allowedUrls: ["https://a.example/"] } }],
				edges: [],
			}) as unknown as GraphWorkflowGraph;

		expect(graphWorkflowAllowedUrls(spelled("tool"))).toEqual(["https://a.example/"]);
		expect(graphWorkflowAllowedUrls(spelled("TOOL"))).toEqual(["https://a.example/"]);
	});
});

const shipped: readonly (readonly [string, GraphWorkflowGraph])[] = GRAPH_WORKFLOW_SAMPLES.map(
	(sample) => [sample.id, sample.graph] as const,
);

describe.each(shipped)("round trip of %s", (_, shippedGraph) => {
	it("survives export → file → import → canvas → wire unchanged", () => {
		const envelope: GraphWorkflowExportEnvelope = {
			format: GRAPH_WORKFLOW_EXPORT_FORMAT,
			formatVersion: GRAPH_WORKFLOW_EXPORT_FORMAT_VERSION,
			name: "Shipped",
			description: null,
			graph: shippedGraph,
		};
		const parsed = parseGraphWorkflowExportFile(JSON.stringify(envelope, null, 2));
		if (!parsed.ok) {
			throw new Error(`refused: ${parsed.reason}`);
		}
		const canvas = graphToCanvas(parsed.envelope.graph);
		const { graph: back, issues } = canvasToGraph(canvas.nodes, canvas.edges, canvas.settings);

		expect(issues).toEqual([]);
		expect(graphWorkflowsEqual(back, shippedGraph)).toBe(true);
	});
});

describe("graphWorkflowImportRequestBytes", () => {
	const envelope = (name: string, description: string): GraphWorkflowExportEnvelope => ({
		format: "xe-graph-workflow",
		formatVersion: 1,
		name,
		description,
		graph,
	});

	// An export is pretty-printed, so a file just over the cap can still post a compact request that fits; the page
	// judges the request, as the route does.
	it("measures the compact request, so a pretty-printed export just over the cap still imports", () => {
		// Ten bytes under the cap compact; the pretty print's newlines and indentation alone push the file past it.
		const base = graphWorkflowImportRequestBytes(envelope("near the cap", ""));
		const near = envelope("near the cap", "x".repeat(GRAPH_WORKFLOW_IMPORT_MAX_BYTES - base - 10));
		const pretty = `${JSON.stringify(near, null, 2)}\n`;

		expect(new TextEncoder().encode(pretty).length).toBeGreaterThan(GRAPH_WORKFLOW_IMPORT_MAX_BYTES);
		expect(graphWorkflowImportRequestBytes(near)).toBeLessThanOrEqual(GRAPH_WORKFLOW_IMPORT_MAX_BYTES);
	});

	it("counts UTF-8 bytes, not characters", () => {
		expect(graphWorkflowImportRequestBytes(envelope("ä", ""))).toBe(graphWorkflowImportRequestBytes(envelope("a", "")) + 1);
	});
});
