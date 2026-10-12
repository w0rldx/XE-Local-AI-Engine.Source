// The file a definition is shared as: the export route's body, and what the import route takes back. The server is the
// authority on the graph; this shape check only refuses a file that is not an export at all, before a dialog opens.

import type { GraphWorkflowGraph } from "@/features/graphWorkflows/models/GraphWorkflowModels";

export const GRAPH_WORKFLOW_EXPORT_FORMAT = "xe-graph-workflow";
export const GRAPH_WORKFLOW_EXPORT_FORMAT_VERSION = 1;

export interface GraphWorkflowExportEnvelope {
	readonly format: typeof GRAPH_WORKFLOW_EXPORT_FORMAT;
	readonly formatVersion: typeof GRAPH_WORKFLOW_EXPORT_FORMAT_VERSION;
	readonly name: string;
	readonly description: string | null;
	readonly graph: GraphWorkflowGraph;
}

/** Every reason a file is refused; each is the last segment of an i18n key under `pages.graphWorkflows.importFile`. */
const graphWorkflowImportFailures = [
	"tooLarge",
	"notJson",
	"wrongFormat",
	"unsupportedVersion",
	"graphNotObject",
	"graphMalformed",
] as const;
export type GraphWorkflowImportFailure = (typeof graphWorkflowImportFailures)[number];

/** The import route's body cap (1 MiB, like create), judged on the COMPACT request the page posts, not the file. */
export const GRAPH_WORKFLOW_IMPORT_MAX_BYTES = 1024 * 1024;

/**
 * The most the picker reads at all. An export is pretty-printed, so a file can be larger than the compact request it
 * becomes; only a file no pretty-printing could explain is refused unread.
 */
export const GRAPH_WORKFLOW_IMPORT_READ_LIMIT_BYTES = 8 * GRAPH_WORKFLOW_IMPORT_MAX_BYTES;

/** The UTF-8 size of the request an import of `envelope` posts: the compact envelope, as `JSON.stringify` sends it. */
export function graphWorkflowImportRequestBytes(envelope: GraphWorkflowExportEnvelope): number {
	return new TextEncoder().encode(JSON.stringify(envelope)).length;
}

export type GraphWorkflowExportParse =
	| { readonly ok: true; readonly envelope: GraphWorkflowExportEnvelope }
	| { readonly ok: false; readonly reason: GraphWorkflowImportFailure };

function isObject(value: unknown): value is Record<string, unknown> {
	return value !== null && typeof value === "object" && !Array.isArray(value);
}

export function parseGraphWorkflowExportFile(text: string): GraphWorkflowExportParse {
	let value: unknown;
	try {
		value = JSON.parse(text);
	} catch {
		return { ok: false, reason: "notJson" };
	}
	if (!isObject(value) || value["format"] !== GRAPH_WORKFLOW_EXPORT_FORMAT) {
		return { ok: false, reason: "wrongFormat" };
	}
	if (value["formatVersion"] !== GRAPH_WORKFLOW_EXPORT_FORMAT_VERSION) {
		return { ok: false, reason: "unsupportedVersion" };
	}
	const graph = value["graph"];
	if (!isObject(graph)) {
		return { ok: false, reason: "graphNotObject" };
	}
	// Only the two lists the dialog walks are checked; what is inside them is the server's to judge.
	if (!Array.isArray(graph["nodes"]) || !Array.isArray(graph["edges"])) {
		return { ok: false, reason: "graphMalformed" };
	}
	return {
		ok: true,
		envelope: {
			format: GRAPH_WORKFLOW_EXPORT_FORMAT,
			formatVersion: GRAPH_WORKFLOW_EXPORT_FORMAT_VERSION,
			name: typeof value["name"] === "string" ? value["name"] : "",
			description: typeof value["description"] === "string" ? value["description"] : null,
			graph: graph as GraphWorkflowGraph,
		},
	};
}

/**
 * `<slug>.graph-workflow.json`, spelled exactly as the export route's `GraphWorkflowShareFile.FileName` does: accents
 * folded, ASCII letters and digits lowercased, every other run one hyphen, at most 64 characters.
 */
export function graphWorkflowExportFileName(name: string): string {
	const slug = name
		.normalize("NFD")
		.replace(/[\u0300-\u036f]/g, "")
		.toLowerCase()
		.replace(/[^a-z0-9]+/g, "-")
		.replace(/^-+/, "")
		.slice(0, 64)
		.replace(/-+$/, "");
	return `${slug.length > 0 ? slug : "graph-workflow"}.graph-workflow.json`;
}

/**
 * Every `allowedUrls` prefix a Tool node in the graph fetches under, deduplicated, in graph order. Read off a file
 * nobody has validated yet, so a non-list or a non-object entry contributes nothing rather than throwing at render.
 */
export function graphWorkflowAllowedUrls(graph: GraphWorkflowGraph): readonly string[] {
	const nodes: readonly unknown[] = Array.isArray(graph.nodes) ? graph.nodes : [];
	// The server parses `kind` case-insensitively (GraphWorkflowTokens.TryParseName), so "tool" imports and runs; the
	// preview must disclose its prefixes too.
	const urls = nodes.flatMap((node) => {
		const allowed =
			isObject(node) && typeof node["kind"] === "string" && node["kind"].toLowerCase() === "tool" && isObject(node["config"])
				? node["config"]["allowedUrls"]
				: undefined;
		return Array.isArray(allowed) ? allowed.filter((entry): entry is string => typeof entry === "string") : [];
	});
	return [...new Set(urls)];
}
